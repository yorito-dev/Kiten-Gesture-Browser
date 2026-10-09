using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using TabBrowser.Models;
using TabBrowser.Services;
using TabBrowser.Services.Workspaces;
using TabBrowser.ViewModels;
using TabBrowser.Views;
using WorkspaceFolderItem = TabBrowser.Models.Workspaces.FolderItem;
using WorkspaceSessionTab = TabBrowser.Models.Workspaces.SessionTab;

namespace TabBrowser;

public partial class MainWindow : Window, INotifyPropertyChanged, ILinkPreviewHost, IUrlThumbnailCaptureHost, IAddressBarHost, IBookmarkPreviewHost, IBookmarkControllerHost, ITabStripHost, ITabReorderHost, ITabMoveUndoHost, ITabDragHost, IFolderTabFileDropHost, ITabDropHost, ITabTransferHost, ITabSelectionHost, IYouTubeControlPanelHost, ITopToolbarHost, IChatGptReadabilityPanelHost, ITabAudioHost, ITabAudioBoostPopupHost, IStreamingPlaybackNoticeHost, IStatusPresentationHost, ITabThumbnailCaptureHost, IBackgroundTabWorkHost, IInactiveTabWorkHost, IWebViewRecoveryHost, ITabActivationHost, ISessionRestorationHost, IClosedTabLifecycleHost, ISessionWindowCaptureHost, IWorkspaceSessionHost, IWorkspaceTabHost, IRecentItemsHost, IWorkspaceNoteEditHost, IBrowsingHistoryHost, IYouTubePlaybackDiagnosticHost

{
    private static int s_nextWorkspaceWindowIndex;
    private static readonly ApplicationWindowCloseCoordinator s_applicationWindowCloseCoordinator = new();

    private const string NewTabUrl = "tabbrowser://newtab";
    private const string WorkspaceTabUrl = WorkspaceTabController.WorkspaceTabUrl;
    private const string UserGuideUrl = "tabbrowser://guide";
    private const string UserGuideVirtualHostName = "kiten-guide.local";
    private const string UserGuideContentUrl = "https://" + UserGuideVirtualHostName + "/user-guide.html";
    private const double BackgroundWebViewWidth = 1024;
    private const double BackgroundWebViewHeight = 640;
    private const double ParkedWebViewOffset = -32000;
    private const int YouTubeNavigationBackgroundWorkDeferralMilliseconds = 8000;
    private const int YouTubeNavigationCompletedBackgroundWorkDeferralMilliseconds = 5000;
    private const int RestoredWindowStartupActivationDelayStepMilliseconds = 450;
    private const int RestoredWindowStartupActivationMaxDelayMilliseconds = 2400;
    private const int TabHoverPrewarmDelayMilliseconds = 350;
    private const int TabMemoryManagementIntervalMilliseconds = 60000;
    private const int LinkPreviewHoverDelayMilliseconds = 450;
    private const int PrivateBrowsingCleanupTimeoutMilliseconds = 5000;
    private const int WindowTaskDrainTimeoutMilliseconds = 5000;
    private const string TabDragTransferFormat = "TabBrowser.TabTransfer.v1";
    private const string SiteInformationUrlDragFormat = "TabBrowser.SiteInformationUrl.v1";
    private const string TabDragStatusPrefix = "ドラッグ中: ";
    private const int TabMoveUndoDisplayMilliseconds = 8000;
    private const double ExtensionPopupWidth = 380;
    private const double ExtensionPopupHeight = 560;
    private static readonly TabNavigationHistoryService s_tabNavigationHistoryService = new(IsPersistableNavigationHistoryUrl);
    private const int WorkspaceOpenTabsRefreshDelayMilliseconds = 250;
    private const int RightWheelTabCyclingTimeoutMilliseconds = 5000;
    private const int LowLevelMouseHook = 14;
    private const int WmKeyDown = 0x0100;
    private const int WmSystemKeyDown = 0x0104;
    private const int WmChar = 0x0102;
    private const int WmUniChar = 0x0109;
    private const int WmMouseMove = 0x0200;
    private const int WmRightButtonDown = 0x0204;
    private const int WmMouseWheel = 0x020A;
    private const int WmImeChar = 0x0286;
    private const int WmRightButtonUp = 0x0205;
    private const int WmCancelMode = 0x001F;
    private const int WmMouseActivate = 0x0021;
    private const int WmNcHitTest = 0x0084;
    private const int WmEnterSizeMove = 0x0231;
    private const int WmExitSizeMove = 0x0232;
    private const int MaActivate = 1;
    private const int MouseKeyStateRightButton = 0x0002;
    private const int VirtualKeyShift = 0x10;
    private const int VirtualKeyControl = 0x11;
    private const int VirtualKeyMenu = 0x12;
    private const int HitTestLeft = 10;
    private const int HitTestRight = 11;
    private const int HitTestTop = 12;
    private const int HitTestTopLeft = 13;
    private const int HitTestTopRight = 14;
    private const int HitTestBottom = 15;
    private const int HitTestBottomLeft = 16;
    private const int HitTestBottomRight = 17;
    private const double YouTubeSpeedStep = 0.25;
    private const double YouTubeControlPopupFallbackWidth = YouTubeControlPanelPresentation.YouTubeControlPopupFallbackWidth;
    private const double YouTubeControlPopupEdgeMargin = YouTubeControlPanelPresentation.YouTubeControlPopupEdgeMargin;
    private const double YouTubeControlPopupFullscreenEdgeMargin = YouTubeControlPanelPresentation.YouTubeControlPopupFullscreenEdgeMargin;
    private const double YouTubeControlPopupBottomMargin = 6;
    private const double YouTubeControlPopupFullscreenBottomMargin = 4;
    private const int YouTubeControlIdleHideDelayMilliseconds = YouTubeControlPanelController.YouTubeControlIdleHideDelayMilliseconds;
    private const int YouTubeControlIdleCheckDelayMilliseconds = YouTubeControlIdleHideDelayMilliseconds + 100;
    private const int YouTubePlaybackStateFallbackDelayMilliseconds = 30000;
    private const int YouTubePlaybackProgressSaveIntervalMilliseconds = 30000;
    private const int TopToolbarAutoHideDelayMilliseconds = 900;
    private const long TabAudioPanDiagnosticLogMaxBytes = 256 * 1024;
    private static readonly string[] ExternalUrlDropFormats =
    [
        "UniformResourceLocatorW",
        "UniformResourceLocator",
        "URL",
        "text/x-moz-url"
    ];
    private static readonly string[] ExternalTextDropFormats =
    [
        DataFormats.UnicodeText,
        DataFormats.Text,
        DataFormats.StringFormat,
        "text/plain"
    ];

    private readonly ObservableCollection<BrowserTab> _tabs = [];
    private readonly ObservableCollection<ExtensionToolbarItem> _extensionToolbarItems = [];
    private readonly ObservableCollection<BookmarkItem> _bookmarks = [];
    private readonly ObservableCollection<BookmarkFolderItem> _bookmarkFolders = [];
    private static readonly ClosedTabWindowHistoryStore s_closedTabWindowHistoryStore = new();
    private static readonly SessionRecoveryDiagnosticState s_sessionRecoveryDiagnosticState = new();
    private static readonly PreviewThumbnailRenderer s_previewThumbnailRenderer = new(
        () => VisualTreeHelper.GetDpi(Application.Current.MainWindow).PixelsPerDip);
    private static readonly TabRuntimeCoordinator s_tabRuntimeCoordinator =
        TabRuntimeCoordinator.Shared;
    private static readonly TabAudioPanFrameSubscriptionOwner s_tabAudioPanFrameSubscriptionOwner = new(
        QueueTabAudioPanFrameInitializeForCurrentOwner,
        QueueTabAudioPanFrameApplyForCurrentOwner,
        RegisterTabAudioPanFrameForCurrentOwner);
    private static readonly SessionRuntimeCaptureAdapter s_sessionRuntimeCaptureAdapter = new(
        static () => s_closedTabWindowHistoryStore.CreateClosedTabSnapshot(),
        static () => s_closedTabWindowHistoryStore.CreateClosedWindowSnapshot(),
        CreateClosedMediaHistorySnapshot,
        AppendSessionDiagnostic);
    private static readonly SessionPersistenceCoordinator s_sessionPersistenceCoordinator = new(
        static () => DateTimeOffset.Now,
        SessionPersistenceDispatcherBridge.Schedule,
        SessionPersistenceDispatcherBridge.Cancel,
        CaptureLatestSessionPersistenceWorkItemAsync,
        CaptureLatestSessionPersistenceWorkItem,
        HandleScheduledSessionSaveFailure);
    private readonly ClosedTabLifecycleController _closedTabLifecycleController;
    private readonly Dictionary<Guid, long> _webViewNavigationStartTimestamps = [];
    private readonly HashSet<Guid> _newWindowPopupTabIds = [];
    private readonly SessionRestorationController _sessionRestorationController;
    private readonly WorkspaceSessionController _workspaceSessionController;
    private readonly WorkspaceTabController _workspaceTabController;
    private readonly RecentItemsController _recentItemsController;
    private readonly WorkspaceNoteEditController _workspaceNoteEditController;
    private readonly BrowsingHistoryController _browsingHistoryController;
    private readonly YouTubePlaybackDiagnosticController _youTubePlaybackDiagnosticController;
    private readonly FolderTabOpenController _folderTabOpenController;
    private readonly TabThumbnailCaptureController _tabThumbnailCaptureController;
    private readonly SettingsService _settingsService = new();
    private readonly SessionService _sessionService = new(s_sessionRecoveryDiagnosticState);
    private readonly BookmarkController _bookmarkController;
    private readonly BookmarkMenuController _bookmarkMenuController;

    private readonly TabCaptureService _tabCaptureService = new(s_tabRuntimeCoordinator);
    private readonly TabOperationService _tabOperationService = new();
    private readonly TabDragDropService _tabDragDropService = new();
    private readonly StartupTabActivationService _startupTabActivationService = new();
    private readonly TabActivationWorkCoordinator _tabActivationWorkCoordinator = new();
    private readonly NativeTabAudioPanController _nativeTabAudioPanController = new();
    private readonly TabAudioController _tabAudioController;
    private readonly BackgroundTabWorkCoordinator _backgroundTabWorkCoordinator = new();
    private readonly BackgroundTabWorkController _backgroundTabWorkController;
    private readonly LinkPreviewController _linkPreviewController;
    private readonly InactiveYouTubeTabWorkCoordinator _inactiveYouTubeTabWorkCoordinator = new();
    private readonly InactiveTabWorkController _inactiveTabWorkController;
    private readonly WebViewLifecycleService _webViewLifecycleService = new(s_tabRuntimeCoordinator);
    private readonly TabMemoryManagementService _tabMemoryManagementService = new();
    private readonly TabMemoryEvaluationController _tabMemoryEvaluationController;
    private readonly WebViewRecoveryController _webViewRecoveryController;
    private readonly TabActivationController _tabActivationController;
    private readonly WebViewEnvironmentService _webViewEnvironmentService;
    private readonly ContainerProfileStore _containerProfileStore = new();
    private readonly SplitViewService _splitViewService = new();
    private readonly SplitViewStore _splitViewStore = new();
    private readonly SplitViewWindowStateCoordinator _splitViewWindowStateCoordinator = new();
    private readonly SplitViewDisplayController _splitViewDisplayController;
    private readonly ReadabilityController _readabilityController = new();
    private readonly DownloadPolicy _downloadPolicy = new();
    private readonly DownloadPathResolver _downloadPathResolver = new();
    private readonly DownloadStore _downloadStore = new();
    private readonly DownloadService _downloadService;

    private readonly LinkCopyService _linkCopyService = new();
    private readonly ClipboardWriteService _clipboardWriteService;
    private readonly TabExportService _tabExportService = new(s_tabRuntimeCoordinator);
    private readonly ExternalBrowserService _externalBrowserService = new();
    private readonly YouTubeControlService _youTubeControlService;
    private static readonly InputInteractionHub s_inputInteractionHub = new();
    private static readonly WebViewProcessRecoveryCoordinator s_webViewProcessRecoveryCoordinator = new();
    private static readonly WebViewEnvironmentExtensionModeCoordinator s_webViewEnvironmentExtensionModeCoordinator = new();
    private readonly Guid _workspaceWindowId = Guid.NewGuid();
    private Guid _sessionWindowId = Guid.NewGuid();
    private readonly int _workspaceWindowIndex = ++s_nextWorkspaceWindowIndex;
    private readonly bool _isPrivateWindow;
    private readonly bool _restoreFloatingMediaPlaceholders;
    private readonly PrivateBrowsingContext? _privateBrowsingContext;
    private readonly PrivateBrowsingShutdownCoordinator? _privateBrowsingShutdownCoordinator;
    private readonly WindowShutdownCoordinator _windowShutdownCoordinator = new(
        static (phase, exception) => AppendSessionDiagnostic(
            "window-shutdown-failed",
            exception,
            $"phase={phase}"));
    private NativeWindowInputBlocker? _windowShutdownInputBlocker;
    private readonly WindowTaskSupervisor _windowTaskSupervisor = new(static (operation, exception) =>
        AppendSessionDiagnostic("window-task-failed", exception, $"operation={operation}"));
    private readonly OwnedDispatcherTimer _statusClearTimer;
    private readonly SortedDictionary<int, OwnedDispatcherTimer> _windowShutdownTimers = [];
    private readonly List<OwnedDispatcherTimer> _windowShutdownPhaseTimers = [];
    private readonly TabMoveUndoController _tabMoveUndoController;
    private readonly StatusPresentationController _statusPresentationController;
    private readonly OwnedDispatcherTimer _backgroundLoadDelayTimer;
    private readonly OwnedDispatcherTimer _focusedViewingCpuSaverTimer;
    private readonly OwnedDispatcherTimer _tabMemoryManagementTimer;
    private readonly OwnedDispatcherTimer _tabHoverPrewarmTimer;
    private readonly OwnedDispatcherTimer _dragWindowFollowTimer;
    private readonly OwnedDispatcherTimer _workspaceOpenTabsRefreshTimer;

    private readonly TabStripController _tabStripController;
    private readonly TabReorderController _tabReorderController;
    private readonly TabDragController _tabDragController;
    private readonly TabDropController _tabDropController;
    private readonly TabTransferController _tabTransferController;
    private readonly TabSelectionController _tabSelectionController;
    private readonly FolderTabFileDropController _folderTabFileDropController;
    private readonly OwnedDispatcherTimer _youtubeControlPlacementTimer;
    private readonly YouTubeControlPanelController _youTubeControlPanelController;
    private readonly OwnedDispatcherTimer _youtubePlaybackStateCaptureTimer;
    private readonly OwnedDispatcherTimer _youtubePlaybackProgressSaveTimer;
    private readonly OwnedDispatcherTimer _topToolbarAutoHideTimer;
    private readonly TopToolbarController _topToolbarController;
    private readonly Border _blankView;
    private readonly Views.WorkspaceGestureController _blankViewGestureController;
    private Popup? _extensionPopup;
    private WebView2? _extensionPopupWebView;
    private readonly PopupNavigationRequestCoordinator _extensionPopupRequestCoordinator = new();
    private readonly BookmarkPreviewController _bookmarkPreviewController;
    private WorkspaceOSWindow? _workspaceOSWindow;
    private WindowCloseConfirmationWindow? _windowCloseConfirmationWindow;
    private readonly Dictionary<Guid, WorkspaceFolderWindow> _workspaceFolderWindows = [];

    private AppSettings _settings = new();
    private List<ContainerProfile> _containerProfiles = [];
    private AddressService _addressService = new(new AppSettings());
    private IReadOnlyList<SessionTabState>? _initialTabs;
    private bool _isInitialized;
    private bool _isAlwaysOnTop;
    private bool _isChangingSelection;
    private BrowserTab? _pendingTabHoverPrewarm;
    private FrameworkElement? _pendingTabHoverElement;
    private bool _persistSession = true;
    private bool _skipSingleWindowSessionSaveOnClose;
    private bool _closeConfirmed;
    private bool _externalDownloadShutdownStarted;
    private bool _externalDownloadShutdownCompleted;
    private bool _recordCurrentWindowInClosedHistory;
    private WindowShutdownContext? _pendingWindowShutdownContext;
    private bool _webViewEnvironmentReleasedSuccessfully;
    private readonly AddressBarController _addressBarController;
    private bool _isFollowingDragCursor;
    private string _gestureScript = string.Empty;
    private BrowserTab? _displayedTab;
    private BrowserTab? _tabContextSplitLeftTab;
    private BrowserTab? _tabContextSplitRightTab;
    private readonly TabContextMenuInteractionCoordinator _tabContextMenuInteractionCoordinator = new();
    private ContextMenu? _activeTabContextMenu;
    private BrowserTab? _activeTabContextMenuTab;
    private FrameworkElement? _activeTabContextMenuPlacementTarget;
    private readonly TabAudioBoostPopupController _tabAudioBoostPopupController;
    private Point _activeTabContextMenuPlacementPoint;
    private TabDragPayload? _activeDragPayload;
    private Guid? _lastActiveBrowserTabId;
    private Button? _extensionPopupPlacementTarget;
    private MainWindow? _sourceDrivenExternalTabDropPreviewWindow;
    private readonly LowLevelMouseProc _rightWheelMouseHookProc;
    private IntPtr _rightWheelMouseHookHandle;
    private bool _isClosingWindow;
    private bool _isInteractiveWindowMoveResize;
    private bool _windowChangeWorkPending;
    private readonly YouTubeControlWorkCoordinator _youTubeControlWorkCoordinator = new();
    private ChatGptReadabilityPanelController _chatGptReadabilityPanelController = null!;
    private readonly DownloadShelfController _downloadShelfController;
    private TimeSpan _startupActivationDelay;

    public event PropertyChangedEventHandler? PropertyChanged;

    public double TabItemWidth => _tabStripController?.TabItemWidth ?? TabStripController.PreferredTabWidth;

    public double VerticalTabSidebarWidth
    {
        get => _tabStripController?.VerticalTabSidebarWidth ?? TabLayoutSettingsProfile.DefaultVerticalSidebarWidth;
        private set => _tabStripController.VerticalTabSidebarWidth = value;
    }

    public MainWindow()
        : this(null, persistSession: true, initialPosition: null)
    {
    }

    public MainWindow(SessionTabState initialTab, Point initialPosition)
        : this([initialTab], persistSession: false, initialPosition)
    {
    }

    private MainWindow(
        SessionTabState initialTab,
        Point initialPosition,
        bool isPrivateWindow)
        : this([initialTab], persistSession: false, initialPosition, isPrivateWindow: isPrivateWindow)
    {
    }

    private MainWindow(
        SessionWindowState initialWindow,
        TimeSpan startupActivationDelay = default,
        bool restoreFloatingMediaPlaceholders = false)
        : this(
            null,
            persistSession: false,
            initialPosition: null,
            startupActivationDelay,
            restoreFloatingMediaPlaceholders: restoreFloatingMediaPlaceholders)
    {
        ApplySessionWindowIdentity(initialWindow.WindowId);
        ApplyWindowPlacement(initialWindow.Placement);
        ApplyAlwaysOnTop(initialWindow.IsAlwaysOnTop ?? _settings.AlwaysOnTop);
        RestoreSession(CreateSessionData(initialWindow), restoreClosedTabs: false);
    }

    private MainWindow(
        IReadOnlyList<SessionTabState>? initialTabs,
        bool persistSession,
        Point? initialPosition,
        TimeSpan startupActivationDelay = default,
        bool isPrivateWindow = false,
        bool restoreFloatingMediaPlaceholders = false,
        bool restoreWorkspaceSession = false)
    {
        _clipboardWriteService = new ClipboardWriteService(Dispatcher);
        _pageGestureVisualController = new PageGestureVisualController(this, this);
        _nativeWebGestureController = new NativeWebGestureController(this, s_inputInteractionHub, _workspaceWindowId, _pageGestureVisualController);
        _pageContextMenuController = new PageContextMenuController(this, s_inputInteractionHub, _workspaceWindowId);
        _closedTabLifecycleController = new ClosedTabLifecycleController(
            this, s_closedTabWindowHistoryStore, _splitViewService, _tabOperationService, GetClosedTabLifecycleControllers);
        _commandPaletteDataController = new CommandPaletteDataController(
            _windowTaskSupervisor,
            this,
            new CommandPaletteDataSources(
                CommandPaletteBackgroundLoadCoordinator.FetchWorkspaceSourcesAsync,
                token => App.BrowsingHistory.SearchAsync(new BrowsingHistoryQuery(Limit: 100), token),
                _applicationCatalogService.GetApplicationsAsync,
                new FolderTabIconService().GetIconAsync,
                ApplicationCatalogService.InvalidateCache));
        var addressSuggestionQueryCoordinator = new AddressSuggestionQueryCoordinator(
            _windowTaskSupervisor.LifetimeToken,
            static (query, token) => App.BrowsingHistory.SearchAsync(query, token));
        _isPrivateWindow = isPrivateWindow;
        _privateBrowsingContext = isPrivateWindow
            ? PrivateBrowsingDataService.CreateContext()
            : null;
        _privateBrowsingShutdownCoordinator = _privateBrowsingContext is null
            ? null
            : new PrivateBrowsingShutdownCoordinator();
        _webViewEnvironmentService = new WebViewEnvironmentService(_privateBrowsingContext?.UserDataFolder);
        MarkStartupPerformance(StartupPerformanceStage.WindowConstructorStarted);
        _rightWheelMouseHookProc = RightWheelMouseHookCallback;
        _commandPaletteKeyboardHookProc = CommandPaletteKeyboardHookCallback;
        _commandPaletteController = new CommandPaletteController(
            _commandRegistry,
            _commandSearchIndex,
            _tabCommandProvider,
            _closedTabCommandProvider,
            _bookmarkCommandProvider,
            _historyCommandProvider,
            _containerCommandProvider);
        var _tabLayoutUpdateTimer = RegisterWindowShutdownTimer(new OwnedDispatcherTimer(
            DispatcherTimerOwner.MainWindow,
            DispatcherTimerTerminationPhase.OrderedWindowShutdown,
            9)
        {
            Interval = TimeSpan.FromMilliseconds(TabStripController.TabLayoutResizeDelayMilliseconds)
        });
        var _dropPreviewClearTimer = RegisterWindowShutdownTimer(new OwnedDispatcherTimer(
            DispatcherTimerOwner.MainWindow,
            DispatcherTimerTerminationPhase.OrderedWindowShutdown,
            10)
        {
            Interval = TimeSpan.FromMilliseconds(120)
        });
        var _folderTabFileDropHoverTimer = RegisterWindowShutdownTimer(new OwnedDispatcherTimer(
            DispatcherTimerOwner.MainWindow,
            DispatcherTimerTerminationPhase.OrderedWindowShutdown,
            12)
        {
            Interval = TimeSpan.FromMilliseconds(FolderTabFileDropController.FolderTabFileDropHoverDelayMilliseconds)
        });
        var _commandPaletteSearchRefreshTimer = RegisterWindowShutdownTimer(new OwnedDispatcherTimer(
            DispatcherTimerOwner.MainWindow,
            DispatcherTimerTerminationPhase.OrderedWindowShutdown,
            13)
        {
            Interval = TimeSpan.FromMilliseconds(CommandPaletteViewController.CommandPaletteSearchRefreshDelayMilliseconds)
        });
        var linkPreviewHoverTimer = RegisterWindowShutdownPhaseTimer(new OwnedDispatcherTimer(
            DispatcherTimerOwner.MainWindow,
            DispatcherTimerTerminationPhase.WindowShutdown)
        {
            Interval = TimeSpan.FromMilliseconds(LinkPreviewHoverDelayMilliseconds)
        });
        InitializeComponent();
        _recentClosedItemsController = new RecentClosedItemsController(
            new RecentClosedItemsElements(
                ClosedTabHistoryButton, ClosedTabHistoryPopup, ClosedTabHistoryList,
                RecentClosedTabsFilter, RecentClosedWindowsFilter, RecentClosedYouTubeFilter,
                RecentClosedSearchBox, RecentClosedEmptyTextBlock, RestoreButton),
            this, s_closedTabWindowHistoryStore, _tabOperationService);
        _statusClearTimer = RegisterWindowShutdownTimer(new OwnedDispatcherTimer(
            DispatcherTimerOwner.MainWindow,
            DispatcherTimerTerminationPhase.OrderedWindowShutdown,
            0)
        {
            Interval = TimeSpan.FromSeconds(2.5)
        });
        _statusPresentationController = new StatusPresentationController(
            new StatusPresentationElements(
                RootGrid, WindowBottomResizeRow, StatusPopup, StatusBar, StatusText, StatusBarTranslateTransform),
            _statusClearTimer,
            this);
        _splitViewDisplayController = new SplitViewDisplayController(
            this,
            new SplitViewDisplayElements(
                SplitViewHost, SplitPaneLayoutGrid, SplitLeftPaneHost, SplitRightPaneHost,
                SplitTopRow, SplitDividerRow, SplitBottomRow, SplitLeftColumn, SplitDividerColumn,
                SplitRightColumn, SplitLeftPaneBorder, SplitRightPaneBorder, SplitViewResizeThumb,
                SplitViewStatusText, SplitViewSwapButton, SplitViewToggleOrientationButton,
                SplitViewCloseLeftButton, SplitViewCloseRightButton),
            _splitViewService,
            _splitViewWindowStateCoordinator);
        _tabThumbnailCaptureController = new TabThumbnailCaptureController(this);
        _backgroundTabWorkController = new BackgroundTabWorkController(this, _backgroundTabWorkCoordinator);
        _inactiveTabWorkController = new InactiveTabWorkController(this, _inactiveYouTubeTabWorkCoordinator);
        _tabMemoryEvaluationController = new TabMemoryEvaluationController(this, _tabMemoryManagementService);
        _webViewRecoveryController = new WebViewRecoveryController(this, _webViewLifecycleService, s_webViewProcessRecoveryCoordinator);
        _tabActivationController = new TabActivationController(this, _tabActivationWorkCoordinator, _webViewLifecycleService, _splitViewService, _splitViewWindowStateCoordinator);
        _youTubeMiniPlayerListViewController = new YouTubeMiniPlayerListViewController(
            new YouTubeMiniPlayerListElements(
                YouTubeMiniPlayerListPopup, YouTubeMiniPlayerList, YouTubeMiniPlayerListEmptyText, YouTubeMiniPlayerListSummaryText, YouTubeMiniPlayerMuteAllToggleButton, YouTubeMiniPlayerMinimizeRestoreAllButton, YouTubeMiniPlayerGlobalActionsButton, YouTubeMiniPlayerArrangeMenuButton, YouTubeMiniPlayerRestoreArrangementButton, YouTubeMiniPlayerPlayPauseAllButton, YouTubeMiniPlayerRestoreClosedButton),
            _youTubeMiniPlayerStateSyncCoordinator, _youTubeMiniPlayerListRefreshTimer, this);
        _youTubeMiniPlayerListPopupController = new YouTubeMiniPlayerListPopupController(
            this, new YouTubeMiniPlayerListPopupElements(
                RootGrid, YouTubeMiniPlayerListPopupRoot, YouTubeMiniPlayerListPopup, YouTubeMiniPlayerAbRepeatPopup, YouTubeMiniPlayerSpeedPopup, YouTubeMiniPlayerVolumePopup, YouTubeMiniPlayerItemMorePopup, YouTubeMiniPlayerArrangePopup, YouTubeMiniPlayerGlobalActionsPopup, YouTubeMiniPlayerLinearPlacementPopup),
            _youTubeMiniPlayerListViewController, this);
        _youTubeMiniPlayerListActionsController = new YouTubeMiniPlayerListActionsController(
            new YouTubeMiniPlayerListActionsElements(
                YouTubeMiniPlayerAbRepeatPopup, YouTubeMiniPlayerSpeedPopup, YouTubeMiniPlayerVolumePopup, YouTubeMiniPlayerItemMorePopup, YouTubeMiniPlayerVolumeTitleText, YouTubeMiniPlayerVolumeSlider, YouTubeMiniPlayerVolumeValueText, YouTubeMiniPlayerAudioBoostPanel, YouTubeMiniPlayerAudioBoostValueText, YouTubeMiniPlayerAudioBoostHintText, YouTubeMiniPlayerAbRepeatTitleText, YouTubeMiniPlayerAbRepeatRangeText, YouTubeMiniPlayerAbRepeatSetStartButton, YouTubeMiniPlayerAbRepeatSetEndButton, YouTubeMiniPlayerAbRepeatToggleButton, YouTubeMiniPlayerAbRepeatClearButton, YouTubeMiniPlayerAudioBoostOffButton, YouTubeMiniPlayerAudioBoost3Button, YouTubeMiniPlayerAudioBoost6Button, YouTubeMiniPlayerAudioBoost9Button),
            _youTubeMiniPlayerListVolumeTimer, this);
        _youTubeMiniPlayerAudioController = new YouTubeMiniPlayerAudioController(
            new YouTubeMiniPlayerAudioElements(
                YouTubeMiniPlayerList, YouTubeMiniPlayerAudioFocusBar, YouTubeMiniPlayerActiveAudioFocusBar, YouTubeMiniPlayerActiveAudioFocusText, YouTubeMiniPlayerAudioFocusSelectionText, YouTubeMiniPlayerPlaySelectedOnlyButton, YouTubeMiniPlayerPauseOutsideSelectionButton, YouTubeMiniPlayerMuteOutsideSelectionButton, YouTubeMiniPlayerRestoreAudioFocusButton, YouTubeMiniPlayerActiveAudioFocusRestoreButton),
            s_youTubeMiniPlayerAudioSessionCoordinator, _youTubeMiniPlayerListInteractionCoordinator,
            _youTubeMiniPlayerHoverAudioPreviewTimer, this);
        _youTubeLiveChatController = new YouTubeLiveChatWindowController(this);
        _youTubeMiniPlayerActivityController = new YouTubeMiniPlayerActivityController(this);
        _youTubeMiniPlayerArrangementController = new YouTubeMiniPlayerArrangementController(this, _youTubeMiniPlayerArrangementSessionCoordinator);
        _youTubeMiniPlayerCommandController = new YouTubeMiniPlayerCommandController(this);
        _youTubeMiniPlayerRecoveryController = new YouTubeMiniPlayerRecoveryController(this);
        _localVideoMiniPlayerController = new LocalVideoMiniPlayerController(this);
        _youTubeMiniPlayerLifetimeController = new YouTubeMiniPlayerLifetimeController(
            this, s_youTubeMiniPlayerClosedHistoryStore, s_youTubeMiniPlayerAudioSessionCoordinator);
        _youTubeMiniPlayerPersistenceController = new YouTubeMiniPlayerPersistenceController(
            this, _youTubeMiniPlayerStateSyncCoordinator, s_youTubeMiniPlayerAudioSessionCoordinator,
            _youTubeMiniPlayerSaveTimer, _youTubeMiniPlayerStateCaptureTimer);
        _streamingPlaybackReadinessController = new StreamingPlaybackReadinessController(
            _streamingPlaybackTabStateCoordinator, _windowTaskSupervisor,
            new StreamingPlaybackReadinessHost(
                _tabs.Contains, GetCurrentPageUrl, GetStreamingPlaybackReadinessProbeExecutor,
                UpdateAmazonPrimeVideoNotice));
        _streamingPlaybackNoticeController = new StreamingPlaybackNoticeController(
            new StreamingPlaybackNoticeElements(
                AmazonPrimeVideoNotice, AmazonPrimeVideoNoticePanel, AmazonPrimeVideoNoticeTitle,
                AmazonPrimeVideoNoticeText, AmazonPrimeVideoDetailsButton, AmazonPrimeVideoActionPanel,
                AmazonPrimeVideoOpenButton, WebHost),
            this, _streamingPlaybackTabStateCoordinator, _externalBrowserService,
            _streamingPlaybackNoticeWindowChangeTimer);
        _tabStripController = new TabStripController(
            new TabStripElements(
                TitleBar, TabList, VerticalPinnedTabList, VerticalTabList,
                VerticalTabSidebarColumn, VerticalTabResizeColumn, VerticalTabSidebar,
                VerticalTabResizeThumb, VerticalTabSearchBox, TabLayoutModeButton, VerticalPinnedTabsHeader),
            _tabs,
            _tabLayoutUpdateTimer,
            this);
        _tabReorderController = new TabReorderController(
            _tabs, _tabStripController, _tabDragDropService, _tabOperationService,
            _dropPreviewClearTimer, this);
        _tabDragController = new TabDragController(
            new TabDragElements(this, TabList, TabDragPopup, TabDragPreview, TabDragPreviewText, TabDragWindowPreviewBody),
            _tabs, _tabStripController, _tabReorderController, _tabDragDropService, this);
        _folderTabFileDropController = new FolderTabFileDropController(_tabs, _folderTabFileDropHoverTimer, this);
        _tabDropController = new TabDropController(new TabDropElements(TabList, RootGrid), _tabs,
            _tabReorderController, _tabDragController, _folderTabFileDropController, _tabDragDropService, this);
        _tabTransferController = new TabTransferController(this, _splitViewService, _tabOperationService, s_tabRuntimeCoordinator);
        _bookmarkController = new BookmarkController(
            this,
            _bookmarks,
            _bookmarkFolders,
            App.Bookmarks,
            recordOpenUsage: !_isPrivateWindow);
        _bookmarkMenuController = new BookmarkMenuController(
            OpenBookmarkHierarchyMenuItem, _bookmarks, _bookmarkFolders,
            bookmark => OpenBookmarkAsync(bookmark), message => SetStatus(message, autoClear: true));
        _tabSelectionController = new TabSelectionController(this, _tabOperationService, _tabActivationWorkCoordinator);
        _tabAudioController = new TabAudioController(this);
        _tabAudioBoostPopupController = new TabAudioBoostPopupController(
            new TabAudioBoostPopupElements(
                TabAudioBoostPopup, TabAudioBoostPanel, TabAudioBoostTitleText, TabAudioBoostValueText,
                TabAudioBoostOffButton, TabAudioBoost3Button, TabAudioBoost6Button, TabAudioBoost9Button),
            this);
        _commandPaletteViewController = new CommandPaletteViewController(
            new CommandPaletteElements(
                CommandPaletteOverlay, CommandPaletteOverlaySurface, CommandPalettePanel, CommandPalettePanelTransform,
                CommandPaletteSearchBox, CommandPaletteShowAllCheckBox, CommandPaletteCommandsOnlyCheckBox,
                CommandPaletteList, CommandPaletteNoResultsText, CommandPaletteQuickLookHost),
            _commandPaletteSearchRefreshTimer,
            $"command-palette:{_workspaceWindowId:D}",
            this);
        _pageToolsController = new PageToolsController(
            new PageToolsElements(
                WebHost, PageFindPopup, PageFindPanel, PageFindTextBox, PageFindCountText,
                PageFindCaseSensitiveCheckBox, PageFindWholeWordCheckBox,
                PageZoomPopup, PageZoomButton, PageZoomResetButton), this);
        _bookmarkPreviewController = new BookmarkPreviewController(WebHost, this, _windowTaskSupervisor);
        _addressBarController = new AddressBarController(
            new AddressBarElements(AddressBox, AddressSuggestionList, AddressSuggestionPopup, AddressPasteAndNavigateMenuItem),
            this, _windowTaskSupervisor, addressSuggestionQueryCoordinator);
        _urlThumbnailCaptureController = new UrlThumbnailCaptureController(
            WebHost, ParkedWebViewOffset, this, _windowTaskSupervisor.LifetimeToken);
        _workspaceNoteEditController = new WorkspaceNoteEditController(this);
        _recentItemsController = new RecentItemsController(this, App.RecentItems, _isPrivateWindow);
        _browsingHistoryController = new BrowsingHistoryController(this, _windowTaskSupervisor.LifetimeToken);
        _youTubePlaybackDiagnosticController = new YouTubePlaybackDiagnosticController(this);
        _recentItemThumbnailController = new RecentItemThumbnailController(
            _isPrivateWindow, _windowTaskSupervisor.LifetimeToken,
            GetRecentItemOpenTabs);
        _closedItemThumbnailController = new ClosedItemThumbnailController(this, _windowTaskSupervisor.LifetimeToken);
        _startupWindowRestoreController = new StartupWindowRestoreController(this, _windowTaskSupervisor.LifetimeToken);
        _sessionRestorationController = new SessionRestorationController(
            this, _tabOperationService, s_tabNavigationHistoryService, _splitViewService, _startupWindowRestoreController);
        _workspaceSessionController = new WorkspaceSessionController(this, _splitViewService);
        _workspaceTabController = new WorkspaceTabController(this);
        _folderTabOpenController = new FolderTabOpenController(this, _tabOperationService, _splitViewService);
        _linkPreviewController = new LinkPreviewController(
            new LinkPreviewElements(
                RootGrid, LinkPreviewPopup, LinkPreviewTitleText, LinkPreviewUrlText,
                LinkPreviewImage, LinkPreviewLoadingText),
            linkPreviewHoverTimer,
            _windowTaskSupervisor,
            this,
            _fiveChLiveReadabilityService,
            _readabilityController);
        DevelopmentChannelBadge.Visibility =
            ApplicationChannelService.Current == ApplicationChannel.Development
                ? Visibility.Visible
                : Visibility.Collapsed;
        WindowPlacementVisibilityService.Attach(this);
        InitializeClipboardHistoryIntegration();
        CommandPaletteQuickLookHost.ConfigureDockTarget(CommandPaletteContent);
        InitializeStartupPerformanceTracking();
        MarkStartupPerformance(StartupPerformanceStage.WindowShellInitialized);
        s_inputInteractionHub.RegisterWindow(_workspaceWindowId, EndReplacedInputInteraction);
        s_webViewProcessRecoveryCoordinator.RegisterWindow(_workspaceWindowId, QueueWebViewProcessGroupRecovery);
        _webViewEnvironmentService.BrowserProcessExited += WebViewEnvironmentService_BrowserProcessExited;
        WebHost.LayoutUpdated += WebHost_LayoutUpdated;
        UpdateWindowFocusIndicator();
        InitializeCommandPalette();
        _downloadService = new DownloadService(
            _downloadStore,
            _downloadPolicy,
            Dispatcher,
            persistHistory: !_isPrivateWindow);

        _downloadShelfController = new DownloadShelfController(
            _downloadService,
            new DownloadShelfElements(DownloadKindFilterBox, DownloadSearchBox, DownloadList, DownloadsButton, DownloadShelfPopup),
            () => _settings.Downloads,
            QueueDownloadUiUpdate);
        _downloadShelfController.Initialize();
        ComponentDispatcher.ThreadFilterMessage += ComponentDispatcher_ThreadFilterMessage;
        InstallCommandPaletteKeyboardHook();
        Closed += (_, _) =>
        {
            _windowShutdownInputBlocker?.Dispose();
            _windowShutdownInputBlocker = null;
            CancelAllSitePermissionPrompts();
            ModelessWindowService.CloseOwned<SitePrivacyWindow>(this);
            s_inputInteractionHub.UnregisterWindow(_workspaceWindowId);
            s_webViewProcessRecoveryCoordinator.UnregisterWindow(_workspaceWindowId);
            ComponentDispatcher.ThreadFilterMessage -= ComponentDispatcher_ThreadFilterMessage;
            UninstallCommandPaletteKeyboardHook();
            _commandPaletteViewController.Dispose();
            _tabDropController.Dispose();
            _folderTabFileDropController.Dispose();
            _tabDragController.Dispose();
            _statusPresentationController.Dispose();
            _tabMoveUndoController?.Dispose();
            _tabReorderController.Dispose();
            _tabStripController.Dispose();
            _commandPaletteDataController.Dispose();
            _addressBarController.Dispose();
            _bookmarkController.Dispose();
            _youTubeControlPanelController?.Dispose();
            _topToolbarController?.Dispose();
            _streamingPlaybackNoticeController?.Dispose();
            _windowTaskSupervisor.Dispose();
            UninstallRightWheelMouseHook();
            _linkPreviewController.Dispose();
            _performanceDiagnosticLogController.Dispose();
            CommandPaletteQuickLookHost.Dispose();
            DisposeClipboardHistoryIntegration();
            if (_pendingWindowShutdownContext?.RequestSessionSaveAfterFinalClose == true)
            {
                s_sessionPersistenceCoordinator.RequestSave();
            }

            ShutdownApplicationIfNoVisibleMainWindowsRemain();
        };

        _initialTabs = initialTabs;
        _persistSession = persistSession;
        _restoreFloatingMediaPlaceholders = persistSession || restoreFloatingMediaPlaceholders;
        _startupActivationDelay = startupActivationDelay;
        _settings = _settingsService.Load();
        ReloadContainerProfiles(applyToTabs: false);
        _settings.Downloads = DownloadSettingsProfile.Normalize(_settings.Downloads);
        _settings.TabLayout = TabLayoutSettingsProfile.Normalize(_settings.TabLayout);
        _settings.FolderTabViewMode = FolderTabViewMode.Normalize(_settings.FolderTabViewMode);
        _settings.FolderTabSortMode = FolderTabSortMode.Normalize(_settings.FolderTabSortMode);
        _settings.FolderTabSortDirection = FolderTabSortDirection.Normalize(_settings.FolderTabSortDirection);
        _settings.ClipboardHistory = ClipboardHistorySettingsProfile.Normalize(_settings.ClipboardHistory);
        _settings.BrowsingHistory = BrowsingHistorySettingsProfile.Normalize(_settings.BrowsingHistory);
        _settings.SitePrivacy = SitePrivacySettingsProfile.Normalize(_settings.SitePrivacy);
        App.BrowsingHistory.Configure(_settings.BrowsingHistory);
        _settings.QuickLook = QuickLookSettingsProfile.Normalize(_settings.QuickLook);
        InitializePageTools();
        CommandPaletteQuickLookHost.PaneLayoutChanged += QuickLookPaneLayoutChanged;
        CommandPaletteQuickLookHost.TextDisplayChanged += QuickLookTextDisplayChanged;
        CommandPaletteQuickLookHost.Configure(_settings.QuickLook);
        _settings.CommandPaletteUsage = CommandPaletteUsageSettingsProfile.Normalize(_settings.CommandPaletteUsage);
        VerticalTabSidebarWidth = _settings.TabLayout.VerticalSidebarWidth;
        _downloadService.ApplyHistoryRetentionLimit(_settings.Downloads.HistoryRetentionLimit);
        _settings.ChatGptReadability = ChatGptReadabilityProfile.Normalize(_settings.ChatGptReadability);
        InitializeChatGptReadabilityPanel();
        _youTubeControlService = new YouTubeControlService(
            () => _tabs,
            tab => _tabs.Contains(tab),
            tab => ReferenceEquals(tab, ActiveTab),
            IsYouTubeSpeedDisplayTarget,
            GetCurrentPageUrl,
            GetTabWebMessageToken,
            ExecuteYouTubeScriptAsync,
            SetStatus,
            QueueSessionSave,
            UpdateYouTubeSpeedDisplay,
            UpdateYouTubeLoopDisplay,
            s_tabRuntimeCoordinator);
        _isAlwaysOnTop = _settings.AlwaysOnTop;
        Topmost = _isAlwaysOnTop;

        _addressService = new AddressService(_settings);
        _bookmarkController.Load();

        if (_persistSession)
        {
            ApplyWindowPlacement(_settings.WindowPlacement);
        }
        else if (initialPosition is { } position)
        {
            ApplyInitialWindowPosition(position);
        }

        InitializeVerticalTabs();
        ExtensionIconList.ItemsSource = _extensionToolbarItems;
        _addressBarController.InitializeSuggestions();
        _tabs.CollectionChanged += (_, args) =>
        {
            TrackAddedTabRuntimeOwnership(args);
            RefreshTabLayoutAfterTabCollectionChanged();
            RefreshVerticalTabViews();
        };
        _blankView = CreateBlankView();
        _blankViewGestureController = new Views.WorkspaceGestureController(
            _blankView,
            _settings,
            ExecuteGestureCommandAsync,
            TryBeginBlankViewGesture,
            CompleteWorkspaceGesture,
            TryBeginBlankViewRightWheel,
            CanStartBlankViewGesture,
            gestureScope: GestureCommandScope.Global);
        ShowPlaceholder(_blankView);

        if (!_persistSession && _initialTabs is not null)
        {
            _initialTabs = restoreWorkspaceSession
                ? _workspaceSessionController.FilterRestorableInitialTabs(_initialTabs)
                : _initialTabs;
            RestoreSession(CreateInitialSession(_initialTabs), restoreClosedTabs: false);
        }

        var _tabMoveUndoTimer = RegisterWindowShutdownTimer(new OwnedDispatcherTimer(
            DispatcherTimerOwner.MainWindow,
            DispatcherTimerTerminationPhase.OrderedWindowShutdown,
            11)
        {
            Interval = TimeSpan.FromMilliseconds(TabMoveUndoDisplayMilliseconds)
        });
        _tabMoveUndoController = new TabMoveUndoController(this, TabMoveUndoButton, _tabMoveUndoTimer);

        _backgroundLoadDelayTimer = RegisterWindowShutdownTimer(new OwnedDispatcherTimer(
            DispatcherTimerOwner.MainWindow,
            DispatcherTimerTerminationPhase.OrderedWindowShutdown,
            1));
        _backgroundLoadDelayTimer.Tick += (_, _) =>
        {
            _backgroundLoadDelayTimer.Stop();
            QueueBackgroundTabLoading();
            QueueBackgroundThumbnailWarmup();
        };

        _focusedViewingCpuSaverTimer = RegisterWindowShutdownTimer(new OwnedDispatcherTimer(
            DispatcherTimerOwner.MainWindow,
            DispatcherTimerTerminationPhase.OrderedWindowShutdown,
            3));
        _focusedViewingCpuSaverTimer.Tick += (_, _) =>
        {
            _focusedViewingCpuSaverTimer.Stop();
            QueueFocusedViewingCpuSaver();
        };

        _tabMemoryManagementTimer = RegisterWindowShutdownTimer(new OwnedDispatcherTimer(
            DispatcherTimerOwner.MainWindow,
            DispatcherTimerTerminationPhase.OrderedWindowShutdown,
            4)
        {
            Interval = TimeSpan.FromMilliseconds(TabMemoryManagementIntervalMilliseconds)
        });
        _tabMemoryManagementTimer.Tick += async (_, _) => await EvaluateInactiveTabMemoryAsync();

        _tabHoverPrewarmTimer = RegisterWindowShutdownTimer(new OwnedDispatcherTimer(
            DispatcherTimerOwner.MainWindow,
            DispatcherTimerTerminationPhase.OrderedWindowShutdown,
            5)
        {
            Interval = TimeSpan.FromMilliseconds(TabHoverPrewarmDelayMilliseconds)
        });
        _tabHoverPrewarmTimer.Tick += async (_, _) => await PrewarmHoveredTabAsync();

        _dragWindowFollowTimer = RegisterWindowShutdownTimer(new OwnedDispatcherTimer(
            DispatcherTimerOwner.MainWindow,
            DispatcherTimerTerminationPhase.OrderedWindowShutdown,
            6)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        });
        _dragWindowFollowTimer.Tick += (_, _) => UpdateDragWindowFollowPosition();

        _workspaceOpenTabsRefreshTimer = RegisterWindowShutdownTimer(new OwnedDispatcherTimer(
            DispatcherTimerOwner.MainWindow,
            DispatcherTimerTerminationPhase.OrderedWindowShutdown,
            7)
        {
            Interval = TimeSpan.FromMilliseconds(WorkspaceOpenTabsRefreshDelayMilliseconds)
        });
        _workspaceOpenTabsRefreshTimer.Tick += (_, _) =>
        {
            _workspaceOpenTabsRefreshTimer.Stop();
            RefreshLoadedWorkspaceOpenTabs();
        };

        _youtubeControlPlacementTimer = RegisterWindowShutdownTimer(new OwnedDispatcherTimer(
            DispatcherTimerOwner.MainWindow,
            DispatcherTimerTerminationPhase.OrderedWindowShutdown,
            14)
        {
            Interval = TimeSpan.FromMilliseconds(YouTubeControlIdleCheckDelayMilliseconds)
        });
        _youTubeControlPanelController = new YouTubeControlPanelController(
            new YouTubeControlPanelElements(
                YouTubeControlPopup, YouTubeControlPanel,
                YouTubeSpeedDownButton, YouTubeSpeedResetButton, YouTubeSpeedUpButton, YouTubeLoopButton,
                SplitLeftYouTubeControlPopup, SplitLeftYouTubeControlPanel,
                SplitLeftYouTubeSpeedDownButton, SplitLeftYouTubeSpeedResetButton,
                SplitLeftYouTubeSpeedUpButton, SplitLeftYouTubeLoopButton,
                SplitRightYouTubeControlPopup, SplitRightYouTubeControlPanel,
                SplitRightYouTubeSpeedDownButton, SplitRightYouTubeSpeedResetButton,
                SplitRightYouTubeSpeedUpButton, SplitRightYouTubeLoopButton,
                WebHost, SplitLeftPaneHost, SplitRightPaneHost),
            this,
            _youTubeControlWorkCoordinator,
            _windowTaskSupervisor,
            _youtubeControlPlacementTimer);

        _youtubePlaybackStateCaptureTimer = RegisterWindowShutdownTimer(new OwnedDispatcherTimer(
            DispatcherTimerOwner.MainWindow,
            DispatcherTimerTerminationPhase.OrderedWindowShutdown,
            15)
        {
            Interval = TimeSpan.FromMilliseconds(YouTubePlaybackStateFallbackDelayMilliseconds)
        });
        _youtubePlaybackStateCaptureTimer.Tick += async (_, _) =>
        {
            _youtubePlaybackStateCaptureTimer.Stop();
            if (!_youTubeControlWorkCoordinator.TryTakePlaybackStateFallback(out var tabId)
                || _tabs.FirstOrDefault(tab => tab.Id == tabId) is not { } tab
                || !ReferenceEquals(tab, ActiveTab)
                || !IsYouTubeVideoTab(tab))
            {
                return;
            }

            if (await CaptureYouTubePlaybackStateAsync(tab))
            {
                QueueSessionSave();
            }
        };

        _youtubePlaybackProgressSaveTimer = RegisterWindowShutdownTimer(new OwnedDispatcherTimer(
            DispatcherTimerOwner.MainWindow,
            DispatcherTimerTerminationPhase.OrderedWindowShutdown,
            16)
        {
            Interval = TimeSpan.FromMilliseconds(YouTubePlaybackProgressSaveIntervalMilliseconds)
        });
        _youtubePlaybackProgressSaveTimer.Tick += (_, _) =>
        {
            _youtubePlaybackProgressSaveTimer.Stop();
            if (!_youTubeControlWorkCoordinator.TakePendingPlaybackProgressSave())
            {
                return;
            }

            QueueSessionSave();
        };

        InitializeFloatingMediaTimers();

        _topToolbarAutoHideTimer = RegisterWindowShutdownPhaseTimer(new OwnedDispatcherTimer(
            DispatcherTimerOwner.MainWindow,
            DispatcherTimerTerminationPhase.WindowShutdown)
        {
            Interval = TimeSpan.FromMilliseconds(TopToolbarAutoHideDelayMilliseconds)
        });
        _topToolbarController = new TopToolbarController(
            new TopToolbarElements(TitleBar, ToolbarRow, TopToolbar, AddressBox,
                TopToolbarScrollViewer, TopToolbarScrollLeftButton, TopToolbarScrollRightButton,
                TopToolbarGrid, AddressToolbarColumn, AddressArea, AddressAreaOutline),
            this, _topToolbarAutoHideTimer);

        ApplyToolbarVisibility();
        SyncXReadableModeMenuItem();
        UpdateWindowMaximizeRestoreButton();
        InitializeStreamingPlaybackNoticeWindowTracking();
        LocationChanged += (_, _) =>
        {
            QueueWindowChangeWork();
        };
        SizeChanged += (_, _) => QueueWindowChangeWork();
    }

    private BrowserTab? ActiveTab => TabList.SelectedItem as BrowserTab;

    TabLayoutSettings ITabStripHost.Settings
    {
        get => _settings.TabLayout;
        set => _settings.TabLayout = value;
    }

    BrowserTab? ITabStripHost.ActiveTab => ActiveTab;

    bool ITabStripHost.IsChangingSelection => _isChangingSelection;

    void ITabStripHost.NotifyLayoutPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    void ITabStripHost.UpdateHostedWebViewSizes() => UpdateHostedWebViewSizes();

    void ITabStripHost.SaveSettings() => SettingsService.Save(_settings);

    void ITabStripHost.SelectAndActivateTab(BrowserTab tab)
    {
        SelectTab(tab);
        QueueSelectedTabActivation();
    }

    void ITabStripHost.RunWithSelectionSuppressed(Action action)
    {
        var wasChangingSelection = _isChangingSelection;
        _isChangingSelection = true;
        try
        {
            action();
        }
        finally
        {
            _isChangingSelection = wasChangingSelection;
        }
    }

    private void InitializeVerticalTabs()
        => _tabStripController?.Initialize();

    private void ApplyTabLayoutSettings()
        => _tabStripController?.ApplyTabLayoutSettings();

    private bool IsVerticalTabMode()
        => _tabStripController?.IsVerticalTabMode() == true;

    private void RefreshVerticalTabViews()
        => _tabStripController?.RefreshVerticalTabViews();

    ListBox ITabReorderHost.ResolveTabList(object originalSource) => ResolveTabList(originalSource);

    bool ITabReorderHost.CanDropTabOnList(BrowserTab tab, ListBox tabList) => CanDropTabOnList(tab, tabList);

    void ITabReorderHost.EnsureWorkspaceTabIsFirst() => EnsureWorkspaceTabIsFirst();

    int ITabReorderHost.ClampNormalTabInsertionIndex(int index) => ClampNormalTabInsertionIndex(index);

    void ITabReorderHost.SelectTab(BrowserTab tab) => SelectTab(tab);

    void ITabReorderHost.QueueSessionSave() => QueueSessionSave();

    void ITabReorderHost.RegisterReorderUndo() => RegisterReorderUndo();

    void ITabReorderHost.SetStatus(string message, bool autoClear) => SetStatus(message, autoClear);

    bool ITabReorderHost.TryGetCursorPointInTabList(ListBox tabList, out Point point)
        => TryGetCursorPointInTabList(tabList, out point);

    bool ITabMoveUndoHost.IsClosing => _isClosingWindow;

    IReadOnlyList<BrowserTab> ITabMoveUndoHost.Tabs => _tabs;

    string ITabMoveUndoHost.StatusText => _statusPresentationController.StatusText;

    void ITabMoveUndoHost.RestoreTabOrder(IReadOnlyList<BrowserTab> originalOrder) => RestoreTabOrder(originalOrder);

    void ITabMoveUndoHost.QueueSessionSave() => QueueSessionSave();

    void ITabMoveUndoHost.RefreshVerticalTabViews() => RefreshVerticalTabViews();

    void ITabMoveUndoHost.SetStatus(string message, bool autoClear) => SetStatus(message, autoClear);

    void ITabMoveUndoHost.ClearStatus() => ClearStatus();

    bool ITabMoveUndoHost.TryInsertFrom(ITabMoveUndoHost source, BrowserTab tab, int insertionIndex)
        => source is MainWindow sourceWindow
            && InsertExternalTabAtIndex(new TabDragPayload(sourceWindow, tab), insertionIndex);

    void ITabMoveUndoHost.Close() => Close();

    void ITabMoveUndoHost.Activate() => Activate();

    BrowserTab? ITabDragHost.FindTabFromOriginalSource(object originalSource) => FindTabFromOriginalSource(originalSource);

    ListBox ITabDragHost.GetActiveTabDragList() => GetActiveTabDragList();

    ITabDragOperation ITabDragHost.CreateOperation(BrowserTab tab)
    {
        var payload = new TabDragPayload(this, tab, isInteractiveDrag: true);
        _activeDragPayload = payload;
        payload.PrepareInteractiveDragData();
        return payload;
    }

    void ITabDragHost.CompleteOperation()
    {
        _activeDragPayload = null;
        _tabReorderController.ResetDropPreviewTarget();
    }

    void ITabDragHost.SelectTab(BrowserTab tab) => SelectTab(tab);

    void ITabDragHost.SetStatus(string message, bool autoClear) => SetStatus(message, autoClear);

    void ITabDragHost.ClearDragStatus() => ClearTabDragStatus();

    void ITabDragHost.ClearSourceDrivenPreview() => ClearSourceDrivenExternalTabDropPreview();

    void ITabDragHost.ResetWorkspaceHover() => ResetWorkspaceTabDragHover();

    bool ITabDragHost.ShouldSuppressDetachForWorkspaceDrop() => ShouldSuppressTabDetachForWorkspaceDrop();

    bool ITabDragHost.IsCursorOverOtherWindow() => IsCursorOverOtherVisibleTabBrowserWindow();

    bool ITabDragHost.IsPointOutsideWindow(Point point) => IsPointOutsideWindow(point);

    Point ITabDragHost.GetMousePositionInWindow() => Mouse.GetPosition(this);

    Point ITabDragHost.GetCursorScreenPoint() => GetCursorScreenPoint();

    bool ITabDragHost.TryGetCursorPointInTabList(ListBox tabList, out Point point)
        => TryGetCursorPointInTabList(tabList, out point);

    bool ITabDragHost.HasLoadedRuntime(BrowserTab tab) => _webViewLifecycleService.HasLoadedRuntime(tab);

    void ITabDragHost.QueueThumbnailCapture(BrowserTab tab)
        => _windowTaskSupervisor.TryRun("tab-drag-preview-thumbnail-capture", _ => CaptureTabThumbnailAsync(tab));

    BrowserTab? IFolderTabFileDropHost.ActiveTab => ActiveTab;

    bool IFolderTabFileDropHost.TryGetFileDropPaths(IDataObject data, out string[] paths)
        => TryGetFolderFileDropPaths(data, out paths);

    BrowserTab? IFolderTabFileDropHost.FindTabFromOriginalSource(object originalSource) => FindTabFromOriginalSource(originalSource);

    IFolderTabFileDropTarget? IFolderTabFileDropHost.GetFolderTarget(BrowserTab? tab)
        => GetTabHostedContent(tab) as Views.FolderTabView;

    void IFolderTabFileDropHost.SelectTab(BrowserTab tab) => SelectTab(tab);

    Task IFolderTabFileDropHost.ActivateSelectedTabAsync() => ActivateSelectedTabAsync();

    void IFolderTabFileDropHost.ClearDropPreview() => ClearDropPreview();

    void IFolderTabFileDropHost.ResetWorkspaceHover() => ResetWorkspaceTabDragHover();

    void IFolderTabFileDropHost.ResetTabDetachCandidate() => ResetTabDetachCandidate();

    void IFolderTabFileDropHost.SetStatus(string message, bool autoClear) => SetStatus(message, autoClear);

    void IFolderTabFileDropHost.ClearStatus() => ClearStatus();

    Guid ITabDropHost.WindowId => _workspaceWindowId;

    Guid? ITabDropHost.ActiveBrowserCommandTargetId => GetActiveBrowserCommandTarget()?.Id;

    ListBox ITabDropHost.GetActiveTabDragList() => GetActiveTabDragList();

    BrowserTab? ITabDropHost.FindTabFromOriginalSource(object originalSource) => FindTabFromOriginalSource(originalSource);

    bool ITabDropHost.CanDropTabOnList(BrowserTab tab, ListBox tabList) => CanDropTabOnList(tab, tabList);

    bool ITabDropHost.TryGetSiteInformation(DragEventArgs e, out ITabDropSiteInformation source)
    {
        var found = TryGetSiteInformationUrlDragSnapshot(e, out var snapshot);
        source = snapshot;
        return found;
    }

    bool ITabDropHost.TryGetPayload(DragEventArgs e, out ITabDropPayload payload)
    {
        var found = TryGetTabDragPayload(e, out var nativePayload);
        payload = nativePayload;
        return found;
    }

    bool ITabDropHost.TryGetSnapshot(DragEventArgs e, out ITabDropSnapshot snapshot)
    {
        var found = TryGetTabTransferSnapshot(e, out var nativeSnapshot);
        snapshot = nativeSnapshot;
        return found;
    }

    bool ITabDropHost.TryGetExternalDrop(DragEventArgs e, out ExternalTabDropTarget externalDrop)
        => TryGetExternalTabDrop(e, out externalDrop);

    bool ITabDropHost.HasFileDrop(DragEventArgs e) => TryGetFolderFileDropPaths(e.Data, out _);

    Task<bool> ITabDropHost.TryHandleSiteInformationDropAsync(DragEventArgs e, bool dropOnTabList)
        => TryHandleSiteInformationUrlDropAsync(e, dropOnTabList);

    bool ITabDropHost.TrySaveDraggedTabToActiveWorkspace(BrowserTab tab, Point point)
        => TrySaveDraggedTabToActiveWorkspace(tab, point);

    bool ITabDropHost.InsertExternalTab(ITabDropPayload payload, ListBox list, Point point, object originalSource)
        => InsertExternalTab((TabDragPayload)payload, list, point, originalSource);

    bool ITabDropHost.InsertTransferredTab(ITabDropSnapshot snapshot, ListBox list, Point point, object originalSource)
        => InsertTransferredTab((TabTransferSnapshot)snapshot, list, point, originalSource);

    void ITabDropHost.OpenExternalDrop(ExternalTabDropTarget target, ListBox list, Point point, object originalSource)
        => OpenExternalTabDropInNewTab(target, list, point, originalSource);

    void ITabDropHost.UpdateSourceDrivenExternalPreview() => UpdateSourceDrivenExternalTabDropPreview();

    bool ITabDropHost.TryGetCursorPointInTabList(ListBox list, out Point point)
        => TryGetCursorPointInTabList(list, out point);

    private void UpdateDownloadsButtonState() => _downloadShelfController?.UpdateButtonState();

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && SiteInformationPopup.IsOpen)
        {
            CloseSiteInformationPopup();
            e.Handled = true;
            return;
        }

        if (IsCommandPaletteShortcut(e))
        {
            e.Handled = true;
            ShowCommandPaletteFromShortcut();
            return;
        }

        if (e.Key == Key.Escape
            && CancelFloatingMediaArrangementPreview(showStatus: true))
        {
            e.Handled = true;
            return;
        }

        if (IsCommandPaletteOpen && TryHandleCommandPaletteKey(e))
        {
            return;
        }

        if (TryHandleBrowserShortcutKey(e))
        {
            return;
        }

        if (e.Key == Key.Escape && _youTubeControlWorkCoordinator.IsApplicationFullscreenActive)
        {
            e.Handled = true;
            QueueYouTubeApplicationFullscreenExit();
            return;
        }

        if (IsSearchSelectedTextShortcut(e.Key, Keyboard.Modifiers))
        {
            e.Handled = true;
            QueueActivePageSelectionSearch();
            return;
        }

        if (TryHandleFolderTabShortcutKey(e))
        {
            return;
        }

        if (TryHandleWorkspaceShortcutKey(e))
        {
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        if (IsCommandPaletteOpen && !IsCommandPalettePanelHit(e))
        {
            CloseCommandPalette();
        }

        base.OnPreviewMouseDown(e);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        if (new WindowInteropHelper(this).Handle is { } handle && handle != IntPtr.Zero)
        {
            HwndSource.FromHwnd(handle)?.AddHook(WindowMessageHook);
        }

        WindowTopmostService.Apply(this, _isAlwaysOnTop);
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmEnterSizeMove)
        {
            BeginInteractiveWindowMoveResize();
        }
        else if (msg == WmExitSizeMove)
        {
            CompleteInteractiveWindowMoveResize();
        }

        if (msg == WmMouseActivate && GetMouseActivateMessage(lParam) == WmRightButtonDown)
        {
            handled = true;
            return new IntPtr(MaActivate);
        }

        if (msg == WmNcHitTest && TryGetResizeHitTest(hwnd, lParam, out var hitTest))
        {
            handled = true;
            return new IntPtr(hitTest);
        }

        return IntPtr.Zero;
    }

    private static int GetMouseActivateMessage(IntPtr parameter)
    {
        return (int)((parameter.ToInt64() >> 16) & 0xffff);
    }

    private bool TryGetResizeHitTest(IntPtr hwnd, IntPtr lParam, out int hitTest)
    {
        hitTest = 0;
        if (WindowState == WindowState.Maximized
            || ResizeMode is ResizeMode.NoResize
            || !GetWindowRect(hwnd, out var bounds))
        {
            return false;
        }

        var point = GetScreenPointFromLParam(lParam);
        var border = GetResizeHitTestThickness();
        var isLeft = point.X < bounds.Left + border;
        var isRight = point.X >= bounds.Right - border;
        var isTop = point.Y < bounds.Top + border;
        var isBottom = point.Y >= bounds.Bottom - border;

        hitTest = (isTop, isBottom, isLeft, isRight) switch
        {
            (true, _, true, _) => HitTestTopLeft,
            (true, _, _, true) => HitTestTopRight,
            (_, true, true, _) => HitTestBottomLeft,
            (_, true, _, true) => HitTestBottomRight,
            (true, _, _, _) => HitTestTop,
            (_, true, _, _) => HitTestBottom,
            (_, _, true, _) => HitTestLeft,
            (_, _, _, true) => HitTestRight,
            _ => 0
        };

        return hitTest != 0;
    }

    private int GetResizeHitTestThickness()
    {
        var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        return Math.Max(6, (int)Math.Ceiling(8 * scale));
    }

    private static Point GetScreenPointFromLParam(IntPtr lParam)
    {
        var value = lParam.ToInt64();
        return new Point(unchecked((short)(value & 0xffff)), unchecked((short)((value >> 16) & 0xffff)));
    }

    private bool TryHandleWorkspaceShortcutKey(KeyEventArgs e)
    {
        if (ActiveTab is not { IsWorkspaceTab: true } activeTab
            || GetTabHostedContent(activeTab) is not Views.WorkspaceOSView workspaceView)
        {
            return false;
        }

        var ignoreTextInputFocus = ReferenceEquals(Keyboard.FocusedElement, AddressBox);
        if (!workspaceView.TryHandleShortcutKey(e.Key, ignoreTextInputFocus))
        {
            return false;
        }

        e.Handled = true;
        return true;
    }

    private void ScheduleLinkPreview(
        BrowserTab sourceTab, string? rawUrl, string? rawTitle, double? clientX, double? clientY)
        => _linkPreviewController.ScheduleLinkPreview(
            CaptureLinkPreviewSource(sourceTab), rawUrl, rawTitle, clientX, clientY);

    private LinkPreviewSource? CaptureLinkPreviewSource(BrowserTab sourceTab)
        => _tabs.Contains(sourceTab) && IsLinkPreviewSourceVisible(sourceTab)
            ? new LinkPreviewSource(
                sourceTab.Id, GetCurrentPageUrl(sourceTab) ?? sourceTab.Url, GetTabWebView(sourceTab))
            : null;

    bool ILinkPreviewHost.IsClosingWindow => _isClosingWindow;

    bool ILinkPreviewHost.IsLinkHoverPreviewEnabled => _settings.EnableLinkHoverPreview;

    LinkPreviewSource? ILinkPreviewHost.ResolveVisibleSource(Guid tabId)
        => _tabs.FirstOrDefault(tab => tab.Id == tabId) is { } tab
            ? CaptureLinkPreviewSource(tab)
            : null;

    Task<ImageSource> ILinkPreviewHost.CaptureThumbnailAsync(
        string url, string title, CancellationToken cancellationToken)
        => _urlThumbnailCaptureController.CaptureLinkPreviewAsync(url, title, cancellationToken);

    bool ILinkPreviewHost.TryGetCachedThumbnail(string url, out ImageSource thumbnail)
        => _urlThumbnailCaptureController.TryGetCached(url, out thumbnail);

    void ILinkPreviewHost.StoreCachedThumbnail(string url, ImageSource thumbnail)
        => _urlThumbnailCaptureController.StoreCached(url, thumbnail);

    void ILinkPreviewHost.StopPreviewPlayback() => StopCommandPaletteUrlThumbnailPlayback();

    void ILinkPreviewHost.ShowHoverStatus(string text) => SetStatus(text);

    void ILinkPreviewHost.ClearHoverStatusIfCurrent(string text)
        => _statusPresentationController.ClearStatusIfCurrent(text);

    private void HideLinkPreview(bool stopPreviewPlayback = false)
        => _linkPreviewController.HideLinkPreview(stopPreviewPlayback);

    private void DismissLinkPreview() => _linkPreviewController.DismissLinkPreview();

    private void HandleLinkHoverEnd(BrowserTab sourceTab)
        => _linkPreviewController.HandleLinkHoverEnd(sourceTab.Id);

    private bool IsLinkPreviewSourceVisible(BrowserTab sourceTab)
    {
        if (ReferenceEquals(ActiveTab, sourceTab))
        {
            return true;
        }

        return IsSplitViewVisible()
            && ActiveTab is { } activeTab
            && _splitViewService.Contains(activeTab)
            && _splitViewService.Contains(sourceTab);
    }

    bool IUrlThumbnailCaptureHost.IsClosingWindow => _isClosingWindow;

    Task<CoreWebView2Environment> IUrlThumbnailCaptureHost.EnsureEnvironmentAsync()
        => EnsureWebViewEnvironmentAsync();

    ImageSource IUrlThumbnailCaptureHost.CreateFallbackThumbnail(string title, string? url)
        => CreateFallbackThumbnail(title, url);

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isInitialized)
        {
            return;
        }

        _isInitialized = true;
        MarkStartupPerformance(StartupPerformanceStage.WindowLoaded);
        ApplyClipboardHistorySettings();
        SetStatus("設定を読み込み中");
        DeferBackgroundTabLoading();

        MarkStartupPerformance(StartupPerformanceStage.GestureScriptLoadStarted);
        var gestureScriptTask = LoadScriptAsync("GestureHost.js");
        Task<SessionData>? sessionLoadTask = null;
        if (_tabs.Count == 0 && _initialTabs is null)
        {
            MarkStartupPerformance(StartupPerformanceStage.SessionLoadStarted);
            sessionLoadTask = _sessionService.LoadAsync(
                recoverEmptySessionFromProtected: ApplicationStabilityService.PreviousRunEndedUnexpectedly);
        }

        var startupRestoreHadFailure = false;
        var restoreWarningCountBefore = _sessionRestorationController.WarningCount;
        SessionLoadInfo? sessionLoadInfo = null;
        try
        {
            SetStatus("セッション復元中");
            if (_tabs.Count == 0)
            {
                SessionData session;
                if (_initialTabs is not null)
                {
                    session = CreateInitialSession(_initialTabs);
                }
                else
                {
                    session = await sessionLoadTask!;
                    MarkStartupPerformance(StartupPerformanceStage.SessionLoadCompleted, itemCount: session.Tabs.Count);
                    sessionLoadInfo = _sessionService.LastLoadInfo;
                }

                if (_persistSession && _initialTabs is null && HasSessionWindows(session))
                {
                    RestoreWindowSet(session);
                }
                else
                {
                    RestoreSession(session);
                }
            }
            else if (ActiveTab is null)
            {
                SelectTab(_tabs[0]);
            }
        }
        catch (Exception ex)
        {
            startupRestoreHadFailure = true;
            AppendSessionDiagnostic("startup-session-restore-failed", ex);
            SetStatus("セッション復元に失敗しました: " + ex.Message, autoClear: true);
        }

        _gestureScript = await gestureScriptTask;
        MarkStartupPerformance(StartupPerformanceStage.GestureScriptLoadCompleted);

        var workspaceTab = _tabs.Count == 0
            ? EnsureWorkspaceTabOpen(activate: true)
            : EnsureWorkspaceTabOpen(activate: false);
        QueueWorkspaceTabWarmup(workspaceTab);

        if (!startupRestoreHadFailure)
        {
            SetStatus("復元したタブを読み込み中");
        }

        RestoreFloatingMediaPlaceholders();
        QueueStartupSelectedTabActivation(sessionLoadInfo, restoreWarningCountBefore, startupRestoreHadFailure);
    }

    private void QueueStartupSelectedTabActivation(
        SessionLoadInfo? sessionLoadInfo,
        int restoreWarningCountBefore,
        bool startupRestoreHadFailure)
    {
        Dispatcher.BeginInvoke(
            new Action(() => _windowTaskSupervisor.TryRun(
                "startup-selected-tab-activation",
                cancellationToken => ActivateStartupSelectedTabAsync(
                    sessionLoadInfo,
                    restoreWarningCountBefore,
                    startupRestoreHadFailure,
                    cancellationToken))),
            DispatcherPriority.Loaded);
    }

    private Task ActivateStartupSelectedTabAsync(
        SessionLoadInfo? sessionLoadInfo,
        int restoreWarningCountBefore,
        bool startupRestoreHadFailure,
        CancellationToken cancellationToken)
        => _startupWindowRestoreController.ActivateSelectedTabOnStartupAsync(
            this, sessionLoadInfo, restoreWarningCountBefore, startupRestoreHadFailure, cancellationToken);

    private string? CreateRestoredTabLoadFailureStatus()
    {
        var failedRestoredTabCount = _webViewLifecycleService.CountFailedRestoredTabs(
            _tabs,
            _sessionRestorationController.RestoredSessionTabIds);
        return StartupWindowRestoreController.CreateRestoredTabLoadFailureStatus(failedRestoredTabCount);
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (_isInteractiveWindowMoveResize)
        {
            CompleteInteractiveWindowMoveResize();
        }

        UpdateWindowBottomResizeStrip();
        UpdateWindowMaximizeRestoreButton();
        _statusPresentationController.UpdatePopupPlacement();
        PositionAmazonPrimeVideoNotice();
        QueueSessionSave();
    }

    private void UpdateWindowBottomResizeStrip()
    {
        WindowBottomResizeRow.Height = WindowState == WindowState.Normal
            && ResizeMode != ResizeMode.NoResize
                ? new GridLength(8)
                : new GridLength(0);
    }

    private void Window_Activated(object? sender, EventArgs e)
    {
        InstallRightWheelMouseHook();
        RefreshWindowFocusIndicators();
        _statusPresentationController.EnsurePopupNotTopmost();
        ReapplyAlwaysOnTopIfNeeded();
        UpdateToolbarState();
        NotifyVisibleFolderTabsActivated();
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        SuspendYouTubeControlPopupsForInactiveWindow();
        CancelFloatingMediaArrangementPreviewOnWindowDeactivated();
        s_inputInteractionHub.CancelForWindow(_workspaceWindowId);
        EndPageGestureVisual();
        EndRightWheelTabCycling();
        UninstallRightWheelMouseHook();
        RefreshWindowFocusIndicators();
        _statusPresentationController.EnsurePopupNotTopmost();
        QueueAlwaysOnTopReapply();
    }

    private static void RefreshWindowFocusIndicators()
    {
        foreach (var window in GetOpenMainWindows())
        {
            window.UpdateWindowFocusIndicator();
        }
    }

    private void UpdateWindowFocusIndicator()
    {
        TitleBar.Background = IsActive
            ? new SolidColorBrush(Color.FromRgb(32, 42, 58))
            : new SolidColorBrush(Color.FromRgb(21, 24, 32));
        TitleBar.BorderBrush = IsActive
            ? new SolidColorBrush(Color.FromRgb(138, 180, 248))
            : new SolidColorBrush(Color.FromRgb(38, 42, 51));
        var channelLabel = ApplicationChannelService.WindowTitleSuffix;
        var privateLabel = _isPrivateWindow ? " - プライベート" : string.Empty;
        Title = IsActive
            ? $"TabBrowser{channelLabel}{privateLabel} - ウィンドウ {_workspaceWindowIndex} (フォーカス中)"
            : $"TabBrowser{channelLabel}{privateLabel} - ウィンドウ {_workspaceWindowIndex}";
    }

    private void ShowSessionDiagnosticSummaryMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ShowSessionDiagnosticSummary();
    }

    private void OpenSessionDiagnosticLogMenuItem_Click(object sender, RoutedEventArgs e)
    {
        OpenSessionDiagnosticLog();
    }

    private void OpenSessionDiagnosticFolderMenuItem_Click(object sender, RoutedEventArgs e)
    {
        OpenSessionDiagnosticFolder();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractiveTitleBarSource(e.OriginalSource as DependencyObject, includeRightDragArea: false))
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleWindowMaximizeRestore();
            e.Handled = true;
            return;
        }

        BeginTitleBarDrag(e);
    }

    private void TitleBar_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractiveTitleBarSource(e.OriginalSource as DependencyObject, includeRightDragArea: true))
        {
            return;
        }

        SystemCommands.ShowSystemMenu(this, PointToScreen(e.GetPosition(this)));
        e.Handled = true;
    }

    private void TitleBarRightDragAreaContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        AlwaysOnTopMenuItem.IsChecked = _isAlwaysOnTop;
        RestoreRememberedWindowSetMenuItem.IsEnabled = _sessionService.HasWindowMemory();
        RestoreProtectedSessionMenuItem.IsEnabled = _sessionService.HasProtectedRecoveryPoint();
        NamedSessionRestorePointsMenuItem.IsEnabled = !_isPrivateWindow;
    }

    private void AlwaysOnTopMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ApplyAlwaysOnTop(AlwaysOnTopMenuItem.IsChecked);
        QueueSessionSave();
        SetStatus(
            AlwaysOnTopMenuItem.IsChecked
                ? "このウィンドウを常に最前面にしました"
                : "このウィンドウの常に最前面を解除しました",
            autoClear: true);
    }

    private void ApplyAlwaysOnTop(bool enabled)
    {
        _isAlwaysOnTop = enabled;
        if (_youTubeControlWorkCoordinator.TryUpdateApplicationFullscreenRestoreTopmost(enabled))
        {
            return;
        }

        WindowTopmostService.Apply(this, enabled);
        ModelessWindowService.SynchronizeOwnedWindowTopmost(this, enabled);
        QueueAlwaysOnTopReapply(enabled);
    }

    private void ReapplyAlwaysOnTopIfNeeded()
    {
        if (!_isClosingWindow
            && !_youTubeControlWorkCoordinator.IsApplicationFullscreenActive
            && _isAlwaysOnTop)
        {
            WindowTopmostService.Apply(this, enabled: true);
            ModelessWindowService.SynchronizeOwnedWindowTopmost(this, enabled: true);
        }
    }

    private void QueueAlwaysOnTopReapply(bool expectedEnabled = true)
    {
        Dispatcher.BeginInvoke(
            () =>
            {
                if (_isAlwaysOnTop == expectedEnabled)
                {
                    ReapplyAlwaysOnTopIfNeeded();
                }
            },
            DispatcherPriority.ContextIdle);
    }

    private async void RememberWindowSetMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var windows = GetPersistableMainWindows();
        if (windows.Count == 0)
        {
            SetStatus("記憶するウィンドウがありません", autoClear: true);
            return;
        }

        await CaptureYouTubePlaybackStateForSessionAsync(windows);

        var windowMemory = CreateSessionData(windows);
        await AttachFloatingMediaWindowMemorySnapshotAsync(windowMemory, windows);
        try
        {
            await _sessionService.SaveWindowMemoryAsync(windowMemory);
        }
        catch (Exception)
        {
            SetStatus("ウィンドウ構成を記憶できませんでした。既存データを確認してください", autoClear: true);
            return;
        }
        RestoreRememberedWindowSetMenuItem.IsEnabled = true;
        SetStatus("現在のウィンドウ構成を記憶しました", autoClear: true);
    }

    private async void RestoreRememberedWindowSetMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SessionData session;
        try
        {
            session = await _sessionService.LoadWindowMemoryAsync();
        }
        catch (Exception)
        {
            SetStatus("記憶したウィンドウ構成を読み込めませんでした。保存データは変更していません", autoClear: true);
            return;
        }
        _sessionRestorationController.RestoreRememberedWindows(
            session,
            (state, activationDelay) =>
            {
                var window = new MainWindow(state, activationDelay);
                window.Show();
                return window;
            },
            RestoreFloatingMediaWindowMemoryPlaceholders);
    }

    private async void RestoreProtectedSessionMenuItem_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var session = await _sessionService.LoadProtectedRecoveryPointAsync();
            if (session is null)
            {
                SetStatus("利用できる保護復元点はありません", autoClear: true);
                return;
            }

            MergeProtectedClosedItemHistory(session);
            var windows = CreateRememberedWindowStates(session);
            foreach (var item in windows.Select((windowState, index) => new { WindowState = windowState, Index = index }))
            {
                var window = new MainWindow(
                    item.WindowState,
                    GetRestoredWindowStartupActivationDelay(item.Index, windows.Count));
                window.Show();
            }

            QueueSessionSave();
            SetStatus(
                windows.Count > 0
                    ? "保護復元点のウィンドウ構成と履歴を追加復元しました"
                    : "保護復元点の履歴を追加復元しました",
                autoClear: true);
        }
        catch (Exception ex)
        {
            AppendSessionDiagnostic("protected-session-restore-failed", ex);
            SetStatus("保護復元点を復元できませんでした: " + ex.Message, autoClear: true);
        }
    }

    private void NamedSessionRestorePointsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_isPrivateWindow) return;
        ModelessWindowService.Show(new Views.NamedSessionRestorePointsWindow(_sessionService,
            async () =>
            {
                await CaptureYouTubePlaybackStateForSessionAsync(GetPersistableMainWindows());
                return CreateSessionData(GetPersistableMainWindows());
            }, async session =>
            {
                var plan = await Task.Run(() => SessionSnapshotService.CreateNamedRestorePlan(session));
                var deferred = 0;
                var restoredTabs = 0;
                var failedTabs = 0;
                foreach (var item in plan.Windows.Select((state, index) => (state, index)))
                {
                    var expectedCount = item.state.Tabs.Count;
                    item.state.Tabs = _workspaceSessionController.FilterRestorableInitialTabs(item.state.Tabs).ToList();
                    deferred += expectedCount - item.state.Tabs.Count;
                    if (item.state.Tabs.Count == 0)
                    {
                        continue;
                    }
                    try
                    {
                        var window = new MainWindow(item.state,
                            GetRestoredWindowStartupActivationDelay(item.index, plan.Windows.Count));
                        var result = window._sessionRestorationController.LastRestoreResult;
                        restoredTabs += result.RestoredTabCount;
                        failedTabs += result.FailedTabCount;
                        if (result.RestoredTabCount == 0)
                        {
                            window._closeConfirmed = true;
                            window._skipSingleWindowSessionSaveOnClose = true;
                            window.Close();
                            continue;
                        }

                        window.Show();
                    }
                    catch (Exception ex)
                    {
                        failedTabs += item.state.Tabs.Count;
                        AppendSessionDiagnostic("named-session-window-restore-failed", ex,
                            $"window={item.index};tabs={item.state.Tabs.Count}");
                    }
                }
                if (restoredTabs > 0) QueueSessionSave();
                return SessionSnapshotService.CreateNamedRestoreStatus(restoredTabs, deferred, failedTabs);
            })
        { Owner = this });
    }

    private static List<SessionWindowState> CreateRememberedWindowStates(SessionData session)
        => SessionSnapshotService.CreateRememberedWindowStates(session);

    private void WindowMinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        SystemCommands.MinimizeWindow(this);
    }

    private void WindowMaximizeRestoreButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleWindowMaximizeRestore();
    }

    private void WindowCloseButton_Click(object sender, RoutedEventArgs e)
    {
        SystemCommands.CloseWindow(this);
    }

    private void WindowCloseButtonContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        CloseOtherWindowsMenuItem.IsEnabled = GetOpenMainWindows().Any(window => !ReferenceEquals(window, this));
    }

    private async void CloseOtherWindowsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var otherWindows = GetOpenMainWindows()
            .Where(window => !ReferenceEquals(window, this))
            .ToList();
        if (otherWindows.Count == 0)
        {
            SetStatus("閉じる他のウィンドウはありません", autoClear: true);
            return;
        }

        await CaptureYouTubePlaybackStateForSessionAsync(GetOpenMainWindows());
        var protection = await FlushWindowSetPersistenceAsync(otherWindows, saveSettings: true);
        if (!protection.Succeeded)
        {
            SetStatus(protection.FailureMessage, autoClear: false);
            return;
        }
        using (s_applicationWindowCloseCoordinator.EnterTemporaryCloseAllScope())
        {
            var closeCompletions = otherWindows
                .Select(WaitForWindowCloseOrHideAsync)
                .ToList();
            foreach (var window in otherWindows)
            {
                window._recordCurrentWindowInClosedHistory = true;
                window._windowShutdownCoordinator.PreparePersistence(protection);
            }
            try
            {
                foreach (var window in otherWindows)
                {
                    window.Close();
                }

                await Task.WhenAll(closeCompletions);
            }
            finally
            {
                foreach (var window in otherWindows)
                {
                    window._recordCurrentWindowInClosedHistory = false;
                    window._windowShutdownCoordinator.ClearPreparedPersistence();
                }
            }
        }

        var finalSave = await FlushWindowSetPersistenceAsync([this], saveSettings: true, checkClosingEditors: false);
        if (!finalSave.Succeeded)
        {
            SetStatus("Windowは閉じましたが終了後の状態更新に失敗しました。保存先を確認し、再保存を試してください。", autoClear: false);
            return;
        }
        QueueWorkspaceOpenTabsRefresh();
        SetStatus("このウィンドウ以外を閉じました", autoClear: true);
    }

    private static Task WaitForWindowCloseOrHideAsync(MainWindow window)
    {
        if (!window.IsVisible)
        {
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler? closed = null;
        DependencyPropertyChangedEventHandler? visibilityChanged = null;

        void Complete()
        {
            window.Closed -= closed;
            window.IsVisibleChanged -= visibilityChanged;
            completion.TrySetResult();
        }

        closed = (_, _) => Complete();
        visibilityChanged = (_, _) =>
        {
            if (!window.IsVisible)
            {
                Complete();
            }
        };
        window.Closed += closed;
        window.IsVisibleChanged += visibilityChanged;
        if (!window.IsVisible)
        {
            Complete();
        }

        return completion.Task;
    }

    private void ResizeLeftThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeWindowFromThumb(resizeLeft: true, resizeRight: false, resizeBottom: false, e.HorizontalChange, e.VerticalChange);
    }

    private void WindowResizeThumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        BeginInteractiveWindowMoveResize();
    }

    private void WindowResizeThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        CompleteInteractiveWindowMoveResize();
    }

    private void BeginInteractiveWindowMoveResize()
    {
        if (_isInteractiveWindowMoveResize)
        {
            return;
        }

        _isInteractiveWindowMoveResize = true;
        BeginActiveWorkspaceResizePreview();
    }

    private void CompleteInteractiveWindowMoveResize()
    {
        if (!_isInteractiveWindowMoveResize)
        {
            return;
        }

        EndActiveWorkspaceResizePreview();
        _isInteractiveWindowMoveResize = false;
        if (!_windowChangeWorkPending)
        {
            return;
        }

        _windowChangeWorkPending = false;
        QueueWindowChangeWork();
    }

    private void QueueWindowChangeWork()
    {
        RepositionOpenYouTubeControlPopupsAfterWindowChange();
        _statusPresentationController.UpdatePopupPlacement();
        if (_isInteractiveWindowMoveResize)
        {
            _windowChangeWorkPending = true;
            return;
        }

        QueueSessionSave();
        QueueStreamingPlaybackNoticeAfterWindowChange();
        QueueYouTubeControlPlacementAfterWindowChange();
    }

    private void BeginActiveWorkspaceResizePreview()
    {
        if (WorkspaceHost.Visibility == Visibility.Visible
            && WorkspaceHost.Content is Views.WorkspaceOSView workspaceView)
        {
            workspaceView.BeginInteractiveResizePreview();
        }
    }

    private void EndActiveWorkspaceResizePreview()
    {
        if (WorkspaceHost.Content is Views.WorkspaceOSView workspaceView)
        {
            workspaceView.EndInteractiveResizePreview();
        }
    }

    private void ResizeRightThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeWindowFromThumb(resizeLeft: false, resizeRight: true, resizeBottom: false, e.HorizontalChange, e.VerticalChange);
    }

    private void ResizeBottomThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeWindowFromThumb(resizeLeft: false, resizeRight: false, resizeBottom: true, e.HorizontalChange, e.VerticalChange);
    }

    private void ResizeBottomLeftThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeWindowFromThumb(resizeLeft: true, resizeRight: false, resizeBottom: true, e.HorizontalChange, e.VerticalChange);
    }

    private void ResizeBottomRightThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeWindowFromThumb(resizeLeft: false, resizeRight: true, resizeBottom: true, e.HorizontalChange, e.VerticalChange);
    }

    private void ResizeWindowFromThumb(bool resizeLeft, bool resizeRight, bool resizeBottom, double horizontalChange, double verticalChange)
    {
        if (WindowState != WindowState.Normal || ResizeMode == ResizeMode.NoResize)
        {
            return;
        }

        EnsureExplicitWindowSize();

        if (resizeLeft)
        {
            var newWidth = Width - horizontalChange;
            if (newWidth < MinWidth)
            {
                Left += Width - MinWidth;
                Width = MinWidth;
            }
            else
            {
                Left += horizontalChange;
                Width = newWidth;
            }
        }

        if (resizeRight)
        {
            Width = Math.Max(MinWidth, Width + horizontalChange);
        }

        if (resizeBottom)
        {
            Height = Math.Max(MinHeight, Height + verticalChange);
        }
    }

    private void EnsureExplicitWindowSize()
    {
        if (!IsValidWindowLength(Width))
        {
            Width = Math.Max(MinWidth, ActualWidth);
        }

        if (!IsValidWindowLength(Height))
        {
            Height = Math.Max(MinHeight, ActualHeight);
        }
    }

    private void BeginTitleBarDrag(MouseButtonEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            RestoreForTitleBarDrag(e);
        }

        try
        {
            DragMove();
            e.Handled = true;
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void RestoreForTitleBarDrag(MouseButtonEventArgs e)
    {
        var restoreBounds = RestoreBounds;
        if (!IsValidWindowLength(restoreBounds.Width) || !IsValidWindowLength(restoreBounds.Height))
        {
            restoreBounds = new Rect(Left, Top, Math.Max(MinWidth, Width), Math.Max(MinHeight, Height));
        }

        var mouseOnTitleBar = e.GetPosition(TitleBar);
        var titleBarWidth = Math.Max(1, TitleBar.ActualWidth);
        var horizontalRatio = Math.Clamp(mouseOnTitleBar.X / titleBarWidth, 0, 1);
        var screenPoint = PointToScreen(e.GetPosition(this));

        WindowState = WindowState.Normal;
        Width = Math.Max(MinWidth, restoreBounds.Width);
        Height = Math.Max(MinHeight, restoreBounds.Height);
        Left = screenPoint.X - (Width * horizontalRatio);
        Top = screenPoint.Y - Math.Min(Math.Max(1, TitleBar.ActualHeight / 2), 24);
    }

    private void ToggleWindowMaximizeRestore()
    {
        if (ResizeMode == ResizeMode.NoResize)
        {
            return;
        }

        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }

    private void UpdateWindowMaximizeRestoreButton()
    {
        if (WindowMaximizeRestoreButton is null)
        {
            return;
        }

        if (WindowState == WindowState.Maximized)
        {
            WindowMaximizeRestoreButton.Content = "❐";
            WindowMaximizeRestoreButton.ToolTip = "元に戻す";
        }
        else
        {
            WindowMaximizeRestoreButton.Content = "□";
            WindowMaximizeRestoreButton.ToolTip = "最大化";
        }
    }

    private static bool IsInteractiveTitleBarSource(DependencyObject? source, bool includeRightDragArea)
    {
        if (FindVisualParent<Button>(source) is not null
            || FindVisualParent<ListBoxItem>(source) is not null
            || FindVisualParent<ContextMenu>(source) is not null
            || FindVisualParent<MenuItem>(source) is not null)
        {
            return true;
        }

        return includeRightDragArea
            && FindNamedVisualParent<Border>(source, "TitleBarRightDragArea") is not null;
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_windowShutdownCoordinator.IsCompleted)
        {
            return;
        }

        if (_windowShutdownCoordinator.IsStarted)
        {
            e.Cancel = true;
            return;
        }

        if (HasOpenWorkspaceNoteEditor([this]))
        {
            e.Cancel = true;
            _windowShutdownCoordinator.ClearPreparedPersistence();
            SetStatus("メモ編集Windowが開いているため終了を中止しました。メモの保存またはキャンセルを確認してから閉じてください。", autoClear: false);
            FindOpenWorkspaceNoteEditor([this])?.ShowRequiredCloseAction();
            return;
        }

        var openWindows = GetOpenMainWindows();
        var hasOtherMainWindows = openWindows.Any(window => !ReferenceEquals(window, this));

        if (!s_applicationWindowCloseCoordinator.IsClosingAllWindows
            && !_closeConfirmed
            && hasOtherMainWindows)
        {
            e.Cancel = true;
            await ShowWindowCloseConfirmationAsync();
            return;
        }

        if (TryHideWindowForActiveFloatingMedia(e))
        {
            return;
        }

        var shutdownContext = _pendingWindowShutdownContext ??= CaptureWindowShutdownContext();
        if (!_externalDownloadShutdownCompleted && _downloadService.HasActiveExternalDownloads)
        {
            e.Cancel = true;
            if (_externalDownloadShutdownStarted)
            {
                return;
            }

            _externalDownloadShutdownStarted = true;
            SetStatus("ダウンロード停止処理中（途中までのファイルを確定しています）", autoClear: false);
            try
            {
                await _downloadService.StopActiveExternalDownloadsForShutdownAsync();
            }
            finally
            {
                _externalDownloadShutdownCompleted = true;
                _externalDownloadShutdownStarted = false;
            }

            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            {
                Close();
            }

            return;
        }

        e.Cancel = true;
        if (!_windowShutdownCoordinator.TryBegin(new WindowShutdownOperations(
            () => BeginWindowShutdown(shutdownContext),
            DrainWindowWorkAsync,
            () => FlushWindowPersistenceAsync(shutdownContext),
            DisposeWindowResources,
            ReleaseWindowWebEnvironment,
            CleanupPrivateBrowsingDataAsync)))
        {
            return;
        }

        await _windowShutdownCoordinator.Completion;
        if (!_windowShutdownCoordinator.IsCompleted)
        {
            _pendingWindowShutdownContext = null;
            _closeConfirmed = false;
            _skipSingleWindowSessionSaveOnClose = false;
            _recordCurrentWindowInClosedHistory = false;
            SetStatus(_windowShutdownCoordinator.PersistenceResult?.FailureMessage
                ?? "保存が完了していないため終了を中止しました。もう一度終了して再試行してください。", autoClear: false);
            return;
        }
        if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
        {
            try
            {
                Close();
            }
            catch (Exception ex)
            {
                AppendSessionDiagnostic("window-final-close-failed", ex);
            }
        }
    }

    private WindowShutdownContext CaptureWindowShutdownContext()
    {
        var hasOtherPersistableWindows = GetPersistableMainWindows()
            .Any(window => WindowShutdownContext.ShouldCountAsRemainingPersistableWindow(
                ReferenceEquals(window, this),
                window._pendingWindowShutdownContext is not null,
                window._windowShutdownCoordinator.IsStarted));
        return WindowShutdownContext.Create(
            s_applicationWindowCloseCoordinator.IsClosingAllWindows,
            _isPrivateWindow,
            _persistSession,
            _skipSingleWindowSessionSaveOnClose,
            _recordCurrentWindowInClosedHistory,
            hasOtherPersistableWindows);
    }

    private void BeginWindowShutdown(WindowShutdownContext context)
    {
        RunWindowShutdownStep("disable-window-input", () =>
        {
            _windowShutdownInputBlocker ??= NativeWindowInputBlocker.Block([this]);
        });
        if (context.IsClosingAllWindows)
        {
            RunWindowShutdownStep(
                "close-status-popup",
                _statusPresentationController.ClosePopupImmediately);
            RunWindowShutdownStep("hide-window", Hide);
        }

        _isClosingWindow = true;
        _floatingMediaHostLifetime.MarkClosing();
        if (context.RecordCurrentWindowInClosedHistory)
        {
            RunWindowShutdownStep(
                "remember-closed-window",
                () => RememberClosedWindowAndTabs(queueSessionSave: false));
        }

        RunWindowShutdownStep("cancel-site-permission-prompts", CancelAllSitePermissionPrompts);
        RunWindowShutdownStep("close-site-privacy-window", () =>
        {
            ModelessWindowService.CloseOwned<SitePrivacyWindow>(this);
        });
        RunWindowShutdownStep("close-window-confirmation", () =>
        {
            if (_windowCloseConfirmationWindow is not { } closeConfirmationWindow)
            {
                return;
            }

            _windowCloseConfirmationWindow = null;
            closeConfirmationWindow.Close();
        });
        RunWindowShutdownStep("begin-window-task-shutdown", () => _windowTaskSupervisor.BeginShutdown());
        RunWindowShutdownStep(
            "cancel-window-input",
            () => s_inputInteractionHub.CancelForWindow(_workspaceWindowId));
        RunWindowShutdownStep("flush-performance-diagnostics", FlushPerformanceDiagnosticLogLines);
        RunWindowShutdownStep("stop-window-timers", StopWindowTimers);
        RunWindowShutdownStep(
            "refresh-other-window-workspaces",
            QueueWorkspaceOpenTabsRefreshForOtherWindows);
    }

    private async Task DrainWindowWorkAsync()
    {
        var result = await _windowTaskSupervisor.DrainAsync(
            TimeSpan.FromMilliseconds(WindowTaskDrainTimeoutMilliseconds));
        if (result.TimedOut)
        {
            AppendSessionDiagnostic(
                "window-task-drain-timeout",
                new TimeoutException("Window終了時のタスク停止が期限内に完了しませんでした。"),
                $"pending={result.PendingTaskCount}");
        }
    }

    private Task<WindowPersistenceResult> FlushWindowPersistenceAsync(WindowShutdownContext context)
    {
        return FlushWindowSetPersistenceAsync([this],
            context.ShouldSaveSettings || (context.IsClosingAllWindows && context.PersistSession));
    }

    private static bool HasOpenWorkspaceNoteEditor(IReadOnlyList<MainWindow> closingWindows)
        => FindOpenWorkspaceNoteEditor(closingWindows) is not null;

    private static WorkspaceNoteEditWindow? FindOpenWorkspaceNoteEditor(IReadOnlyList<MainWindow> closingWindows)
    {
        foreach (var editor in Application.Current.Windows.OfType<WorkspaceNoteEditWindow>())
        {
            for (Window? owner = editor.Owner; owner is not null; owner = owner.Owner)
            {
                if (owner is MainWindow main && closingWindows.Contains(main)) return editor;
            }
        }
        return null;
    }

    private async Task<WindowPersistenceResult> FlushWindowSetPersistenceAsync(
        IReadOnlyList<MainWindow> closingWindows, bool saveSettings, bool checkClosingEditors = true)
    {
        if (checkClosingEditors && HasOpenWorkspaceNoteEditor(closingWindows))
            return new([new("メモ編集Window（保存またはキャンセルを確認して閉じてください）", WindowPersistenceStatus.Pending)]);

        var windows = Application.Current.Windows.OfType<Window>().ToList();
        var targets = new List<WindowPersistenceTarget>();
        foreach (var window in closingWindows.Where(static window => window._downloadService.HasActiveExternalDownloads))
            targets.Add(new($"download-stop-{window._sessionWindowId}", $"Window {window._workspaceWindowIndex} のダウンロード確定", async () =>
            {
                if (!await window._downloadService.StopActiveExternalDownloadsForShutdownAsync())
                    throw new IOException("ダウンロード停止処理が完了していません。");
            }));
        if (closingWindows.Any(static window => !window._isPrivateWindow))
        {
            var settingsOwner = closingWindows.FirstOrDefault(static window => !window._isPrivateWindow && window._persistSession);
            if (saveSettings && settingsOwner is not null)
                targets.Add(new("settings", "設定", () =>
                {
                    settingsOwner.CaptureWindowPlacement();
                    return Task.Run(SettingsService.CaptureSaveOperation(settingsOwner._settings));
                }));
            targets.Add(new("session", "セッション", async () =>
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await s_sessionPersistenceCoordinator.FlushPendingAsync(timeout.Token);
            }));
            targets.Add(new("floating-media", "浮動メディア", () =>
            {
                var save = _youTubeMiniPlayerPersistenceController.CapturePendingSaveOperation();
                return Task.Run(save);
            }));
            targets.Add(new("recent-items", "最近使った項目", () => App.RecentItems.FlushAsync()));
            targets.Add(new("browsing-history", "閲覧履歴", () => App.BrowsingHistory.FlushAsync()));
            foreach (var window in closingWindows.Where(static window => !window._isPrivateWindow))
                targets.Add(new($"downloads-{window._sessionWindowId}", $"Window {window._workspaceWindowIndex} のダウンロード履歴",
                    () => Task.Run(window._downloadService.CaptureHistorySaveOperation())));
        }

        // 入力だけを一時停止し、WPFのIsEnabledは変えない。保存失敗時も表示と操作状態を元へ戻す。
        using var inputBlocker = NativeWindowInputBlocker.Block(windows);
        return await s_applicationWindowCloseCoordinator.SaveBeforeCloseAsync(targets);
    }

    private void DisposeWindowResources()
    {
        RunWindowShutdownStep("dispose-page-tools", DisposePageTools);
        RunWindowShutdownStep("dispose-download-shelf", () => _downloadShelfController?.Dispose());
        RunWindowShutdownStep(
            "close-extension-popup",
            () => CloseExtensionPopup(disposeWebView: true));
        RunWindowShutdownStep(
            "close-bookmark-preview",
            () => CloseBookmarkPreview(clearNavigation: false, disposeWebView: true));

        RunWindowShutdownStep("close-runtime-diagnostics", CloseRuntimeDiagnosticsWindow);
        RunWindowShutdownStep("dispose-closed-tab-runtimes", DisposeClosedTabRuntimes);
        foreach (var window in _workspaceFolderWindows.Values.ToList())
        {
            RunWindowShutdownStep("close-workspace-folder-window", window.Close);
        }

        _workspaceFolderWindows.Clear();
        RunWindowShutdownStep("close-workspace-os-window", () => _workspaceOSWindow?.Close());
        _workspaceOSWindow = null;
        RunWindowShutdownStep("dispose-window-tab-runtimes", DisposeWindowTabRuntimes);
    }

    private void ReleaseWindowWebEnvironment()
    {
        _webViewEnvironmentReleasedSuccessfully = false;
        var thumbnailReleased = RunWindowShutdownStep(
            "dispose-command-palette-thumbnail-webview",
            DisposeCommandPaletteUrlThumbnailWebView);
        var eventUnsubscribed = RunWindowShutdownStep(
            "unsubscribe-browser-process-exited",
            () => _webViewEnvironmentService.BrowserProcessExited -=
                WebViewEnvironmentService_BrowserProcessExited);
        var environmentReleased = RunWindowShutdownStep(
            "dispose-webview-environment",
            _webViewEnvironmentService.Dispose);
        _webViewEnvironmentReleasedSuccessfully =
            thumbnailReleased && eventUnsubscribed && environmentReleased;
    }

    private async Task CleanupPrivateBrowsingDataAsync()
    {
        if (_privateBrowsingContext is not { } privateContext
            || _privateBrowsingShutdownCoordinator is not { } shutdownCoordinator)
        {
            return;
        }

        if (!_webViewEnvironmentReleasedSuccessfully)
        {
            PrivateBrowsingDataService.RecordCleanupFailure(
                privateContext,
                "webview-environment-release-failed");
            return;
        }

        shutdownCoordinator.TryBeginShutdown();
        PrivateBrowsingDeleteResult deleteResult;
        try
        {
            deleteResult = await shutdownCoordinator.ShutdownAsync(
                static () => { },
                cancellationToken => PrivateBrowsingDataService.DeleteContextAsync(
                    privateContext,
                    cancellationToken),
                TimeSpan.FromMilliseconds(PrivateBrowsingCleanupTimeoutMilliseconds));
        }
        catch (Exception ex)
        {
            deleteResult = new PrivateBrowsingDeleteResult(
                PrivateBrowsingDeleteStatus.Failed,
                0,
                $"window-cleanup-exception:{ex.GetType().Name}");
        }

        if (!deleteResult.IsSuccess
            && deleteResult.FailureReason is not "delete-retries-exhausted")
        {
            PrivateBrowsingDataService.RecordCleanupFailure(
                privateContext,
                deleteResult.FailureReason ?? "window-cleanup-failed");
        }
    }

    private static bool RunWindowShutdownStep(string operation, Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            AppendSessionDiagnostic("window-shutdown-step-failed", ex, $"operation={operation}");
            return false;
        }
    }

    private async Task ShowWindowCloseConfirmationAsync()
    {
        if (_windowCloseConfirmationWindow is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            WindowTopmostService.Apply(existing, enabled: true);
            existing.Activate();
            return;
        }

        var dialog = new WindowCloseConfirmationWindow
        {
            Owner = this
        };
        _windowCloseConfirmationWindow = dialog;

        try
        {
            await ModelessWindowService.ShowAsync(dialog, forceTopmost: true);
        }
        catch (Exception ex)
        {
            SetStatus("終了確認を表示できません: " + ex.Message, autoClear: true);
            return;
        }
        finally
        {
            if (ReferenceEquals(_windowCloseConfirmationWindow, dialog))
            {
                _windowCloseConfirmationWindow = null;
            }
        }

        if (_isClosingWindow || dialog.Result == MessageBoxResult.Cancel)
        {
            return;
        }

        await CompleteWindowCloseConfirmationAsync(dialog.Result);
    }

    private async Task CompleteWindowCloseConfirmationAsync(MessageBoxResult result)
    {
        var openWindows = GetOpenMainWindows();
        var windowsToClose = GetApplicationCloseMainWindows()
            .Where(window => !ReferenceEquals(window, this))
            .ToList();

        if (result == MessageBoxResult.Yes && windowsToClose.Count > 0)
        {
            var protection = await FlushWindowSetPersistenceAsync(windowsToClose.Append(this).ToList(), saveSettings: true);
            if (!protection.Succeeded)
            {
                SetStatus(protection.FailureMessage, autoClear: false);
                FindOpenWorkspaceNoteEditor(windowsToClose.Append(this).ToList())?.ShowRequiredCloseAction();
                return;
            }
            s_applicationWindowCloseCoordinator.BeginPermanentCloseAll();
            foreach (var window in windowsToClose)
            {
                window._windowShutdownCoordinator.PreparePersistence(protection);
                window.Close();
            }
            _windowShutdownCoordinator.PreparePersistence(protection);
        }
        else if (result == MessageBoxResult.No && openWindows.Count > 1)
        {
            _skipSingleWindowSessionSaveOnClose = true;
            _recordCurrentWindowInClosedHistory = true;
        }

        _closeConfirmed = true;
        Close();
    }

    private void StopWindowTimers()
    {
        StopRegisteredWindowTimers(0, 2);
        _backgroundTabWorkCoordinator.ClearThumbnailWarmupRequest();
        StopRegisteredWindowTimers(3, 15);
        _youTubeControlWorkCoordinator.ClearPlaybackStateFallback();
        StopRegisteredWindowTimers(16, 16);
        _youTubeControlWorkCoordinator.ClearPendingPlaybackProgressSave();
        StopWindowShutdownPhaseTimers();
        StopFloatingMediaTimers();
        EndPageGestureVisual();
        ClosePageGestureOverlayWindow();
    }

    // 番号は従来のStopWindowTimersの停止順。状態clearを挟む境界も上で維持する。
    private OwnedDispatcherTimer RegisterWindowShutdownTimer(OwnedDispatcherTimer timer)
    {
        if (timer.Owner != DispatcherTimerOwner.MainWindow
            || timer.TerminationPhase != DispatcherTimerTerminationPhase.OrderedWindowShutdown
            || timer.ShutdownOrder is not { } order)
        {
            throw new ArgumentException("順序付きWindow終了タイマーの所有権が不正です。", nameof(timer));
        }

        _windowShutdownTimers.Add(order, timer);
        return timer;
    }

    private OwnedDispatcherTimer RegisterWindowShutdownPhaseTimer(OwnedDispatcherTimer timer)
    {
        if (timer.Owner != DispatcherTimerOwner.MainWindow
            || timer.TerminationPhase != DispatcherTimerTerminationPhase.WindowShutdown)
        {
            throw new ArgumentException("Window終了タイマーの所有権が不正です。", nameof(timer));
        }

        _windowShutdownPhaseTimers.Add(timer);
        return timer;
    }

    private void StopRegisteredWindowTimers(int firstOrder, int lastOrder)
    {
        foreach (var (order, timer) in _windowShutdownTimers)
        {
            if (order >= firstOrder && order <= lastOrder)
            {
                timer.Stop();
            }
        }
    }

    private void StopWindowShutdownPhaseTimers()
    {
        foreach (var timer in _windowShutdownPhaseTimers)
        {
            timer.Stop();
        }
    }

    private static void ShutdownApplicationIfNoVisibleMainWindowsRemain()
    {
        var application = Application.Current;
        if (application is null
            || GetApplicationCloseMainWindows().Count > 0)
        {
            return;
        }

        s_applicationWindowCloseCoordinator.BeginPermanentCloseAll();
        application.Shutdown();
    }

    internal bool IsWindowShutdownCompleted => _windowShutdownCoordinator.IsCompleted;

    internal void NotifySystemShutdownDeferred()
        => SetStatus("未保存状態の破棄を避けるためOSの終了を中止しました。このアプリを通常の終了操作で保存して閉じてから、OSの終了を再実行してください。", autoClear: false);

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled
            || e.Delta == 0
            || ActiveTab is not { IsWorkspaceTab: true }
            || !IsRightMouseButtonDown())
        {
            return;
        }

        if (TryExecuteRightButtonWheelRoute(e.Delta))
        {
            e.Handled = true;
            SuppressWorkspaceRightClickIfWheelArrived(force: true);
        }
    }

    private void ComponentDispatcher_ThreadFilterMessage(ref MSG msg, ref bool handled)
    {
        if (handled || !IsCommandPaletteInputOwnerActive())
        {
            return;
        }

        if (TryHandleCommandPaletteThreadShortcut(msg))
        {
            handled = true;
            return;
        }

        if (TryHandleCommandPaletteThreadTextInput(msg))
        {
            handled = true;
            return;
        }

        if (msg.message is WmRightButtonUp or WmCancelMode)
        {
            EndRightWheelTabCycling();
            return;
        }

        if (msg.message != WmMouseWheel)
        {
            return;
        }

        var wheelDelta = GetWheelDelta(msg.wParam);
        if (wheelDelta == 0)
        {
            return;
        }

        if (s_inputInteractionHub.IsCurrent(
                InputInteractionKind.RightWheelTabSwitch,
                _workspaceWindowId))
        {
            if (TryContinueRightWheelTabCycling(wheelDelta, msg.wParam))
            {
                handled = true;
            }

            return;
        }

        if (TryBeginRightWheelTabCycling(wheelDelta, msg.wParam))
        {
            handled = true;
        }
    }

    private bool TryBeginRightWheelTabCycling(int wheelDelta, IntPtr wParam)
    {
        if (!IsRightMouseWheelMessage(wParam))
        {
            return false;
        }

        var interaction = s_inputInteractionHub.TryBegin(
            InputInteractionKind.RightWheelTabSwitch,
            _workspaceWindowId,
            tabId: null,
            DateTimeOffset.Now,
            TimeSpan.FromMilliseconds(RightWheelTabCyclingTimeoutMilliseconds));
        if (!interaction.Accepted)
        {
            return false;
        }

        CancelNativeWebGestureForRightWheel();
        SuppressWorkspaceRightClickIfWheelArrived(force: true);

        if (TryExecuteRightButtonWheelRoute(wheelDelta))
        {
            return true;
        }

        if (interaction.Current is { } current)
        {
            s_inputInteractionHub.TryComplete(current.Id);
        }

        return false;
    }

    private bool TryContinueRightWheelTabCycling(int wheelDelta, IntPtr wParam)
    {
        var now = DateTimeOffset.Now;
        if (!IsRightMouseWheelMessage(wParam)
            || !s_inputInteractionHub.IsActive(
                InputInteractionKind.RightWheelTabSwitch,
                _workspaceWindowId,
                now))
        {
            EndRightWheelTabCycling();
            return false;
        }

        s_inputInteractionHub.TryBegin(
            InputInteractionKind.RightWheelTabSwitch,
            _workspaceWindowId,
            tabId: null,
            now,
            TimeSpan.FromMilliseconds(RightWheelTabCyclingTimeoutMilliseconds));
        CancelNativeWebGestureForRightWheel();
        SuppressWorkspaceRightClickIfWheelArrived(force: true);
        return TryExecuteRightButtonWheelRoute(wheelDelta);
    }

    private bool TryExecuteRightButtonWheelRoute(int wheelDelta)
    {
        if (ActiveTab is { IsWorkspaceTab: true } activeTab
            && GetTabHostedContent(activeTab) is Views.WorkspaceOSView activeWorkspaceView
            && IsControlKeyDown()
            && activeWorkspaceView.TrySwitchWorkspaceByWheelDelta(wheelDelta))
        {
            activeWorkspaceView.CancelGesture();
            EndPageGestureVisual();
            CancelActiveGestureOverlay();
            return true;
        }

        var route = wheelDelta < 0 ? "wheelDown" : "wheelUp";
        var activeScope = GestureCommandScope.FromSurface(
            CreateCommandInvocationContext(GetActiveBrowserCommandTarget()).Surface);
        var command = GestureSettingsCatalog.ResolveMappingsForScope(
                _settings.GestureMappings,
                activeScope)
            .FirstOrDefault(x => x.Route.Equals(route, StringComparison.Ordinal));
        if (command is null)
        {
            return false;
        }

        DismissLinkPreview();
        if (ActiveTab is { IsWorkspaceTab: true } workspaceTab
            && GetTabHostedContent(workspaceTab) is Views.WorkspaceOSView workspaceView)
        {
            workspaceView.CancelGesture();
        }

        EndPageGestureVisual();
        CancelActiveGestureOverlay();
        QueueGestureCommandExecution(
            command.Command,
            null,
            GetActiveBrowserCommandTarget());
        return true;
    }

    private void CancelActiveGestureOverlay()
    {
        if (GetTabWebView(ActiveTab)?.CoreWebView2 is not { } core)
        {
            return;
        }

        _ = CancelActiveGestureOverlayAsync(core);
    }

    private static async Task CancelActiveGestureOverlayAsync(CoreWebView2 core)
    {
        try
        {
            await core.ExecuteScriptAsync("window.__tabBrowserGestureHostCancel?.();");
        }
        catch
        {
            // ページ遷移中などで実行できない場合は、次回の入力で通常状態へ戻ります。
        }
    }

    private void BeginRightWheelTabCyclingIfNeeded()
    {
        if (!IsRightMouseButtonDown())
        {
            return;
        }

        s_inputInteractionHub.TryBegin(
            InputInteractionKind.RightWheelTabSwitch,
            _workspaceWindowId,
            tabId: null,
            DateTimeOffset.Now,
            TimeSpan.FromMilliseconds(RightWheelTabCyclingTimeoutMilliseconds));
        SuppressWorkspaceRightClickIfWheelArrived();
    }

    private void EndRightWheelTabCycling()
    {
        var wasActive = IsRightWheelTabCyclingActive();
        s_inputInteractionHub.TryComplete(
            InputInteractionKind.RightWheelTabSwitch,
            _workspaceWindowId);
        _tabSelectionController.CompleteCycling(wasActive);
    }

    private bool IsRightWheelTabCyclingActive()
    {
        return s_inputInteractionHub.IsActive(
            InputInteractionKind.RightWheelTabSwitch,
            _workspaceWindowId,
            DateTimeOffset.Now);
    }

    private static int GetWheelDelta(IntPtr wParam)
    {
        return unchecked((short)(((long)wParam >> 16) & 0xffff));
    }

    private static bool IsRightMouseWheelMessage(IntPtr wParam)
    {
        return (((long)wParam) & MouseKeyStateRightButton) == MouseKeyStateRightButton
            || IsRightMouseButtonDown();
    }

    protected override void OnPreviewMouseRightButtonDown(MouseButtonEventArgs e)
    {
        ResetRightClickSuppressionForNewPress();

        var source = e.OriginalSource as DependencyObject;
        var isControlRightClickOnTab = FindVisualParent<ListBoxItem>(source)?.DataContext is BrowserTab
            && Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        _tabContextMenuInteractionCoordinator.BeginInteraction(isControlRightClickOnTab);
        CaptureTabContextSplitSource(source);
        if (isControlRightClickOnTab)
        {
            e.Handled = true;
        }

        base.OnPreviewMouseRightButtonDown(e);
    }

    private void TabContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu)
        {
            return;
        }

        var wasReopening = _tabContextMenuInteractionCoordinator.ConsumeReopening();
        var isReopening = wasReopening
            && ReferenceEquals(menu, _activeTabContextMenu);
        if (!isReopening)
        {
            _activeTabContextMenu = menu;
            _activeTabContextMenuTab = menu.DataContext as BrowserTab;
            _activeTabContextMenuPlacementTarget = menu.PlacementTarget as FrameworkElement;
            if (_activeTabContextMenuPlacementTarget is not null)
            {
                _activeTabContextMenuPlacementPoint = Mouse.GetPosition(_activeTabContextMenuPlacementTarget);
            }
        }

        ConfigureTabContextMenuItems(
            menu.Items,
            _tabContextMenuInteractionCoordinator.IsContinuousMode);
        var contextTab = _activeTabContextMenuTab ?? menu.DataContext as BrowserTab;
        ConfigureTabExternalBrowserMenu(menu, contextTab);
        ConfigureTabYouTubeMiniPlayerMenu(menu, contextTab);
    }

    private static void ConfigureTabYouTubeMiniPlayerMenu(ContextMenu menu, BrowserTab? tab)
    {
        var menuItem = FindMenuItemByTag(menu.Items, "YouTubeMiniPlayerOpen");
        if (menuItem is null)
        {
            return;
        }

        menuItem.IsEnabled = tab is { IsNativeContentTab: false }
            && YouTubeMiniPlayerOpenRequestService.TryCreateFromTab(
                GetCurrentPageUrl(tab) ?? tab.Url,
                tab.Title,
                out _);
    }

    private void ConfigureTabExternalBrowserMenu(ContextMenu menu, BrowserTab? tab)
    {
        var browserMenu = FindMenuItemByTag(menu.Items, "ExternalBrowserMenu");
        if (browserMenu is null)
        {
            return;
        }

        var canOpen = tab is { IsNativeContentTab: false }
            && ExternalBrowserService.IsSupportedWebUrl(tab.Url);
        browserMenu.Visibility = canOpen ? Visibility.Visible : Visibility.Collapsed;
        if (!canOpen)
        {
            return;
        }

        ConfigureExternalBrowserChoices(browserMenu);
    }

    private void ConfigureExternalBrowserChoices(MenuItem browserMenu)
    {
        foreach (var item in browserMenu.Items.OfType<MenuItem>())
        {
            item.IsEnabled = TryGetExternalBrowserKind(item.Tag, out var browserKind)
                && IsExternalBrowserAvailable(browserKind);
        }

        browserMenu.IsEnabled = browserMenu.Items
            .OfType<MenuItem>()
            .Any(static item => item.IsEnabled);
    }

    private static MenuItem? FindMenuItemByTag(ItemCollection items, string tag)
    {
        foreach (var item in items.OfType<MenuItem>())
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.Ordinal))
            {
                return item;
            }

            if (FindMenuItemByTag(item.Items, tag) is { } nestedItem)
            {
                return nestedItem;
            }
        }

        return null;
    }

    private async void OpenTabInExternalBrowserMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem
            && TryGetExternalBrowserKind(menuItem.Tag, out var browserKind)
            && GetTabFromContextMenuItem(menuItem) is { Url: { } url })
        {
            await OpenUrlsInExternalBrowserAsync([url], browserKind);
        }
    }

    private bool IsExternalBrowserAvailable(ExternalBrowserKind browserKind)
    {
        return !_isPrivateWindow
            && !_isClosingWindow
            && _externalBrowserService.IsAvailable(browserKind);
    }

    private Task OpenUrlsInExternalBrowserAsync(
        IReadOnlyList<string> urls,
        ExternalBrowserKind browserKind)
    {
        if (_isPrivateWindow)
        {
            SetStatus("プライベートWindowから外部ブラウザーは開けません", autoClear: true);
            return Task.CompletedTask;
        }

        var result = _externalBrowserService.Open(browserKind, urls);
        if (!result.Succeeded)
        {
            SetStatus(result.ErrorMessage, autoClear: true);
            return Task.CompletedTask;
        }

        var browserName = ExternalBrowserService.GetDisplayName(browserKind);
        SetStatus(
            result.OpenedCount == 1
                ? $"{browserName}で開きました"
                : $"{result.OpenedCount}件を{browserName}で開きました",
            autoClear: true);
        return Task.CompletedTask;
    }

    private static bool TryGetExternalBrowserKind(object? value, out ExternalBrowserKind browserKind)
    {
        browserKind = default;
        return value is string text
            && Enum.TryParse(text, ignoreCase: false, out browserKind);
    }

    private async void DuplicateTabMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem
            && GetTabFromContextMenuItem(menuItem) is { } tab)
        {
            await ExecuteRegisteredCommandAsync(
                BrowserCommandProvider.DuplicateTabActionId,
                tab);
        }
    }

    private void TabContextMenu_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_tabContextMenuInteractionCoordinator.IsContinuousMode
            || sender is not ContextMenu menu
            || !ReferenceEquals(menu, _activeTabContextMenu)
            || FindVisualParent<MenuItem>(e.OriginalSource as DependencyObject) is not { } item
            || item.Items.Count > 0
            || string.Equals(item.Tag as string, "ContinuousModeIndicator", StringComparison.Ordinal))
        {
            return;
        }

        _tabContextMenuInteractionCoordinator.MarkItemExecutionPending();
        var interactionVersion = _tabContextMenuInteractionCoordinator.CaptureVersion();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_tabContextMenuInteractionCoordinator.IsCurrent(interactionVersion)
                && menu.IsOpen)
            {
                _tabContextMenuInteractionCoordinator.ClearItemExecutionPending();
            }
        }), DispatcherPriority.ContextIdle);
    }

    private void TabContextMenu_Closed(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu
            || !ReferenceEquals(menu, _activeTabContextMenu))
        {
            return;
        }

        if (_tabContextMenuInteractionCoordinator.IsContinuousMode
            && _tabContextMenuInteractionCoordinator.IsItemExecutionPending
            && _activeTabContextMenuTab is { } tab
            && _activeTabContextMenuPlacementTarget is { } placementTarget)
        {
            _tabContextMenuInteractionCoordinator.ClearItemExecutionPending();
            QueueTabContextMenuReopen(menu, tab, placementTarget, _activeTabContextMenuPlacementPoint);
            return;
        }

        ResetTabContextMenuContinuousMode(menu);
    }

    private void QueueTabContextMenuReopen(
        ContextMenu menu,
        BrowserTab tab,
        FrameworkElement placementTarget,
        Point placementPoint)
    {
        var interactionVersion = _tabContextMenuInteractionCoordinator.CaptureVersion();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_tabContextMenuInteractionCoordinator.IsCurrent(interactionVersion)
                || !_tabContextMenuInteractionCoordinator.IsContinuousMode
                || !ReferenceEquals(menu, _activeTabContextMenu)
                || !ReferenceEquals(tab, _activeTabContextMenuTab)
                || !_tabs.Contains(tab)
                || !placementTarget.IsLoaded
                || menu.IsOpen)
            {
                return;
            }

            _tabContextMenuInteractionCoordinator.MarkReopening();
            menu.PlacementTarget = placementTarget;
            menu.Placement = PlacementMode.RelativePoint;
            menu.HorizontalOffset = placementPoint.X;
            menu.VerticalOffset = placementPoint.Y;
            menu.IsOpen = true;
        }), DispatcherPriority.ContextIdle);
    }

    private void CloseTabContextMenuForTab(BrowserTab tab)
    {
        if (!ReferenceEquals(tab, _activeTabContextMenuTab))
        {
            return;
        }

        var menu = _activeTabContextMenu;
        ResetTabContextMenuContinuousMode(menu);
        if (menu?.IsOpen == true)
        {
            menu.IsOpen = false;
        }
    }

    private void ResetTabContextMenuContinuousMode(ContextMenu? menu)
    {
        _tabContextMenuInteractionCoordinator.Reset();
        _activeTabContextMenu = null;
        _activeTabContextMenuTab = null;
        _activeTabContextMenuPlacementTarget = null;
        if (menu is not null)
        {
            menu.Placement = PlacementMode.MousePoint;
            menu.HorizontalOffset = 0;
            menu.VerticalOffset = 0;
        }
    }

    private static void ConfigureTabContextMenuItems(ItemCollection items, bool isContinuousMode)
    {
        foreach (var item in items.OfType<MenuItem>())
        {
            if (string.Equals(item.Tag as string, "ContinuousModeIndicator", StringComparison.Ordinal))
            {
                item.Visibility = isContinuousMode ? Visibility.Visible : Visibility.Collapsed;
                continue;
            }

            item.StaysOpenOnClick = isContinuousMode && item.Items.Count == 0;
            ConfigureTabContextMenuItems(item.Items, isContinuousMode);
        }
    }

    private void CaptureTabContextSplitSource(DependencyObject? source)
    {
        ClearCapturedSplitTabs();
        if (FindVisualParent<ListBoxItem>(source) is not { DataContext: BrowserTab rightTab }
            || !_tabs.Contains(rightTab)
            || ActiveTab is not { } leftTab
            || !_tabs.Contains(leftTab))
        {
            return;
        }

        _tabContextSplitLeftTab = leftTab;
        _tabContextSplitRightTab = rightTab;
    }

    private BrowserTab? GetCapturedSplitLeftTab(BrowserTab rightTab)
    {
        return _tabContextSplitRightTab == rightTab
            && _tabContextSplitLeftTab is { } leftTab
            && _tabs.Contains(leftTab)
            ? leftTab
            : null;
    }

    private void ClearCapturedSplitTabs()
    {
        _tabContextSplitLeftTab = null;
        _tabContextSplitRightTab = null;
    }

    private void InstallRightWheelMouseHook()
    {
        if (_rightWheelMouseHookHandle != IntPtr.Zero)
        {
            return;
        }

        var moduleName = Process.GetCurrentProcess().MainModule?.ModuleName;
        var moduleHandle = string.IsNullOrWhiteSpace(moduleName)
            ? IntPtr.Zero
            : GetModuleHandle(moduleName);
        _rightWheelMouseHookHandle = SetWindowsHookEx(
            LowLevelMouseHook,
            _rightWheelMouseHookProc,
            moduleHandle,
            0);
    }

    private void UninstallRightWheelMouseHook()
    {
        if (_rightWheelMouseHookHandle == IntPtr.Zero)
        {
            return;
        }

        UnhookWindowsHookEx(_rightWheelMouseHookHandle);
        _rightWheelMouseHookHandle = IntPtr.Zero;
    }

    private IntPtr RightWheelMouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var message = wParam.ToInt32();
            if (message == WmRightButtonDown)
            {
                ResetRightClickSuppressionForNewPress();
                var downData = Marshal.PtrToStructure<LowLevelMouseHookData>(lParam);
                if (!TryBeginNativeWebGestureCandidate(downData.Point))
                {
                    TryBeginPageContextMenuGesture(downData.Point);
                }
            }
            else if (message == WmMouseMove
                && (_pageGestureVisualController.IsTracking || _nativeWebGestureController.IsTracking))
            {
                var moveData = Marshal.PtrToStructure<LowLevelMouseHookData>(lParam);
                QueuePageGestureInputPoint(moveData.Point);
            }
            else if (message == WmRightButtonUp)
            {
                var upData = Marshal.PtrToStructure<LowLevelMouseHookData>(lParam);
                ReleaseWorkspaceRightClickSuppressionAfterNativeRelease();
                TryFinishPageContextMenuGesture(upData.Point);
                FinishNativeWebGesture(upData.Point);
                EndRightWheelTabCycling();
                QueueEndPageGestureAfterNativeRelease();
                QueueEndWorkspaceGestureAfterNativeRelease();
            }

            if (message == WmMouseWheel)
            {
                if (!IsRightMouseButtonDown())
                {
                    return CallNextHookEx(_rightWheelMouseHookHandle, nCode, wParam, lParam);
                }

                var hookData = Marshal.PtrToStructure<LowLevelMouseHookData>(lParam);
                var wheelDelta = unchecked((short)((hookData.MouseData >> 16) & 0xffff));
                if (TryHandleRightWheelFromLowLevelHook(wheelDelta))
                {
                    return new IntPtr(1);
                }
            }
        }

        return CallNextHookEx(_rightWheelMouseHookHandle, nCode, wParam, lParam);
    }

    private bool TryHandleRightWheelFromLowLevelHook(int wheelDelta)
    {
        if (wheelDelta == 0)
        {
            return false;
        }

        if (!Dispatcher.CheckAccess())
        {
            return Dispatcher.Invoke(() => TryHandleRightWheelFromLowLevelHook(wheelDelta));
        }

        if (!IsActive
            || ActiveTab is null
            || !IsRightMouseButtonDown()
            || IsCursorOverMiniPlayerWindow()
            || !IsCursorInsideWindowBounds())
        {
            return false;
        }

        s_inputInteractionHub.TryBegin(
            InputInteractionKind.RightWheelTabSwitch,
            _workspaceWindowId,
            tabId: null,
            DateTimeOffset.Now,
            TimeSpan.FromMilliseconds(RightWheelTabCyclingTimeoutMilliseconds));
        CancelNativeWebGestureForRightWheel();
        SuppressWorkspaceRightClickIfWheelArrived(force: true);

        if (TryExecuteRightButtonWheelRoute(wheelDelta))
        {
            return true;
        }

        EndRightWheelTabCycling();
        return false;
    }

    private static bool IsCursorOverMiniPlayerWindow()
    {
        const uint getRoot = 2;
        if (!GetCursorPos(out var cursor))
        {
            return false;
        }

        var hoveredWindow = WindowFromPoint(cursor);
        var hoveredRoot = hoveredWindow == IntPtr.Zero
            ? IntPtr.Zero
            : GetAncestor(hoveredWindow, getRoot);
        if (hoveredRoot == IntPtr.Zero)
        {
            return false;
        }

        foreach (var window in GetOpenMainWindows())
        {
            foreach (var runtime in window._youTubeMiniPlayerRuntimes.Values)
            {
                if (new WindowInteropHelper(runtime.TaskbarWindow).Handle == hoveredRoot
                    || GetPresentationSourceHandle(runtime.TitleBand) == hoveredRoot
                    || GetPresentationSourceHandle(runtime.Toolbar) == hoveredRoot)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static IntPtr GetPresentationSourceHandle(Visual visual)
    {
        return (PresentationSource.FromVisual(visual) as HwndSource)?.Handle ?? IntPtr.Zero;
    }

    private bool IsCursorInsideWindowBounds()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero
            || !GetWindowRect(hwnd, out var bounds)
            || !GetCursorPos(out var cursor))
        {
            return false;
        }

        return cursor.X >= bounds.Left
            && cursor.X < bounds.Right
            && cursor.Y >= bounds.Top
            && cursor.Y < bounds.Bottom;
    }

    private async void TabList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isChangingSelection)
        {
            return;
        }

        if (e.RemovedItems.Count > 0)
        {
            DismissLinkPreview();
        }

        foreach (var previousTab in e.RemovedItems.OfType<BrowserTab>())
        {
            ResetNativeWebGestureForTab(previousTab);
            s_inputInteractionHub.CancelForTab(_workspaceWindowId, previousTab.Id);
        }

        NoteExplorationActivity();
        RefreshVerticalTabViews();
        await ActivateSelectedTabAsync();
    }

    private void TabList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        QueueTabLayoutUpdate();
    }

    private void VerticalTabSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        => _tabStripController?.VerticalTabSearchBox_TextChanged(sender, e);

    private void VerticalTabList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => _tabStripController?.VerticalTabList_SelectionChanged(sender, e);

    private async void VerticalTabNewTabButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteRegisteredCommandAsync(BrowserCommandProvider.NewTabActionId);
    }

    private async void VerticalTabCloseButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BrowserTab tab)
        {
            await ExecuteRegisteredCommandAsync(
                BrowserCommandProvider.CloseTabActionId,
                tab);
        }
    }

    private async void VerticalTabPinButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BrowserTab tab)
        {
            return;
        }

        await ExecuteRegisteredCommandAsync(
            BrowserCommandProvider.ToggleTabPinnedActionId,
            tab);
    }

    private void TabContainerMenuItem_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem
            || GetTabFromContextMenuItem(menuItem) is not { } tab)
        {
            return;
        }

        PopulateTabContainerMenu(menuItem, tab);
    }

    private static BrowserTab? GetTabFromContextMenuItem(MenuItem menuItem)
    {
        if (menuItem.DataContext is BrowserTab tab)
        {
            return tab;
        }

        var current = menuItem.Parent;
        while (current is not null)
        {
            if (current is System.Windows.Controls.ContextMenu contextMenu)
            {
                if (contextMenu.DataContext is BrowserTab contextMenuTab)
                {
                    return contextMenuTab;
                }

                if (contextMenu.PlacementTarget is FrameworkElement { DataContext: BrowserTab placementTab })
                {
                    return placementTab;
                }

                return null;
            }

            if (current is FrameworkElement { DataContext: BrowserTab parentTab })
            {
                return parentTab;
            }

            current = current switch
            {
                MenuItem parentMenuItem => parentMenuItem.Parent,
                System.Windows.Controls.ContextMenu => null,
                FrameworkElement frameworkElement => frameworkElement.Parent,
                _ => null
            };
        }

        return null;
    }

    private void NewTabContainerContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu)
        {
            return;
        }

        PopulateNewTabContainerMenu(menu);
    }

    private void PopulateTabContainerMenu(MenuItem menuItem, BrowserTab tab)
    {
        menuItem.Items.Clear();
        var profiles = ReloadContainerProfiles();
        foreach (var profile in profiles)
        {
            var isCurrent = string.Equals(
                tab.ContainerProfileId,
                profile.Id,
                StringComparison.OrdinalIgnoreCase);
            var item = CreateContainerProfileMenuItem(profile, isChecked: isCurrent);
            item.IsEnabled = !isCurrent;
            item.StaysOpenOnClick = _tabContextMenuInteractionCoordinator.IsContinuousMode;
            item.Click += async (_, _) => await ReopenTabInContainerAsync(tab, profile.Id);
            menuItem.Items.Add(item);
        }
    }

    private void PopulateNewTabContainerMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        var profiles = ReloadContainerProfiles();
        foreach (var profile in profiles)
        {
            var item = CreateContainerProfileMenuItem(profile, isChecked: false);
            item.Click += (_, _) => AddNewTab(activate: true, profile.Id);
            menu.Items.Add(item);
        }
    }

    private static MenuItem CreateContainerProfileMenuItem(ContainerProfile profile, bool isChecked)
    {
        return new MenuItem
        {
            Header = GetContainerProfileMenuHeader(profile),
            Icon = CreateContainerProfileMenuIcon(profile),
            IsCheckable = true,
            IsChecked = isChecked
        };
    }

    private static string GetContainerProfileMenuHeader(ContainerProfile profile)
    {
        return profile.IsDefault ? "既定コンテナ" : profile.Name;
    }

    private static Border CreateContainerProfileMenuIcon(ContainerProfile profile)
    {
        return new Border
        {
            Width = 10,
            Height = 10,
            Background = CreateContainerProfileMenuBrush(profile.Color),
            BorderBrush = new SolidColorBrush(Color.FromRgb(75, 85, 99)),
            BorderThickness = new Thickness(1)
        };
    }

    private static Brush CreateContainerProfileMenuBrush(string color)
    {
        try
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        }
        catch
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(ContainerProfile.DefaultColor));
        }
    }

    private async Task ReopenTabInContainerAsync(BrowserTab tab, string containerProfileId)
    {
        if (!_tabs.Contains(tab) || tab.IsNativeContentTab)
        {
            return;
        }

        var profile = GetContainerProfile(containerProfileId);
        if (string.Equals(tab.ContainerProfileId, profile.Id, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var targetUrl = NormalizeSessionTabUrl(GetCurrentPageUrl(tab) ?? tab.Url, tab.Title) ?? NewTabUrl;
        var wasActive = ActiveTab == tab;
        var wasSplitTab = _splitViewService.Contains(tab);

        tab.ApplyContainerProfile(profile);
        tab.Url = targetUrl;
        tab.PreferSavedNavigationHistory = false;
        tab.NavigationHistory = IsNewTabUrl(targetUrl) ? [] : [targetUrl];
        tab.NavigationHistoryIndex = tab.NavigationHistory.Count - 1;
        _ = s_tabRuntimeCoordinator.SetThumbnail(tab, null);
        ReleaseTabRuntimeResources(tab);

        if (wasActive || wasSplitTab)
        {
            if (wasActive)
            {
                ShowBlankView();
                SetAddressText(IsNewTabUrl(targetUrl) ? string.Empty : targetUrl);
            }

            await ActivateSelectedTabAsync();
        }
        else
        {
            QueueBackgroundTabLoading();
        }

        RefreshVerticalTabViews();
        QueueSessionSave();
        QueueWorkspaceOpenTabsRefresh();
        SetStatus($"コンテナを切り替えました: {profile.Name}", autoClear: true);
    }

    private async void SplitTabRightMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BrowserTab tab)
        {
            var leftTab = GetCapturedSplitLeftTab(tab) ?? ActiveTab;
            ClearCapturedSplitTabs();
            await ExecuteRegisteredCommandAsync(
                FeatureCommandProvider.SplitTabRightActionId,
                tab,
                leftTab?.Id);
        }
    }

    private async void SplitTabBottomMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BrowserTab tab)
        {
            var topTab = GetCapturedSplitLeftTab(tab) ?? ActiveTab;
            ClearCapturedSplitTabs();
            await ExecuteRegisteredCommandAsync(
                FeatureCommandProvider.SplitTabBottomActionId,
                tab,
                topTab?.Id);
        }
    }

    private void SplitLeftPane_MouseDown(object sender, MouseButtonEventArgs e)
    {
        ActivateSplitPane(SplitViewPane.Left);
    }

    private void SplitRightPane_MouseDown(object sender, MouseButtonEventArgs e)
    {
        ActivateSplitPane(SplitViewPane.Right);
    }

    private async void SplitViewSwapButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteRegisteredCommandAsync(FeatureCommandProvider.SwapSplitPanesActionId);
    }

    private void SwapSplitPanes()
        => _splitViewDisplayController.SwapPanes();

    private async void SplitViewToggleOrientationButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteRegisteredCommandAsync(
            FeatureCommandProvider.ToggleSplitOrientationActionId);
    }

    private void ToggleSplitOrientation()
        => _splitViewDisplayController.ToggleOrientation();

    private async void SplitViewDetachButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteRegisteredCommandAsync(FeatureCommandProvider.DetachSplitActionId);
    }

    private async void SplitViewCloseLeftButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteRegisteredCommandAsync(FeatureCommandProvider.CloseSplitLeftActionId);
    }

    private async void SplitViewCloseRightButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteRegisteredCommandAsync(FeatureCommandProvider.CloseSplitRightActionId);
    }

    private void CloseSplitPane(string pane)
    {
        if (_splitViewService.GetTabForPane(_tabs, pane) is { } tab)
        {
            CloseTab(tab);
        }
    }

    private void SplitViewResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
        => _splitViewDisplayController.ApplyResize(e.HorizontalChange, e.VerticalChange);

    private void SplitViewResizeThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        PersistSplitViewState();
        QueueSessionSave();
    }

    private void SplitViewResizeThumb_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_splitViewDisplayController.ResetRatio(() => e.Handled = true))
        {
            return;
        }

        PersistSplitViewState();
        QueueSessionSave();
        SetStatus("分割サイズを既定に戻しました", autoClear: true);
    }

    private void VerticalTabSidebarResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
        => _tabStripController?.VerticalTabSidebarResizeThumb_DragDelta(sender, e);

    private void VerticalTabSidebarResizeThumb_DragCompleted(object sender, DragCompletedEventArgs e)
        => _tabStripController?.VerticalTabSidebarResizeThumb_DragCompleted(sender, e);

    private void TabLayoutModeButton_Click(object sender, RoutedEventArgs e)
        => _tabStripController?.TabLayoutModeButton_Click(sender, e);

    private void TabList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => _tabDragController.MouseLeftButtonDown(sender, e);

    private void TabList_PreviewMouseMove(object sender, MouseEventArgs e)
        => _tabDragController.MouseMove(sender, e);

    private void TabList_QueryContinueDrag(object sender, QueryContinueDragEventArgs e)
        => _tabDragController.QueryContinueDrag(sender, e);

    private void RegisterReorderUndo()
        => _tabMoveUndoController.RegisterReorder(_tabDragController.OriginalOrder);

    private void RegisterExternalTabMoveUndo(
        TabDragPayload payload,
        BrowserTab targetTab,
        bool closeTargetWindowAfterUndo = false)
        => _tabMoveUndoController.RegisterExternalMove(
            payload.OriginalSourceWindow._tabMoveUndoController,
            payload.IsInteractiveDrag,
            targetTab,
            payload.OriginalInsertionIndex,
            closeTargetWindowAfterUndo);

    private void TabMoveUndoButton_Click(object sender, RoutedEventArgs e)
        => _tabMoveUndoController?.Undo();

    private void RestoreTabOrder(IReadOnlyList<BrowserTab> originalOrder)
        => _tabReorderController.RestoreTabOrder(originalOrder);

    private void TabList_DragOver(object sender, DragEventArgs e)
        => _tabDropController.TabList_DragOver(sender, e);

    private void TabList_GiveFeedback(object sender, GiveFeedbackEventArgs e)
        => _tabDropController.TabList_GiveFeedback(sender, e);

    private void RootGrid_DragOver(object sender, DragEventArgs e)
        => _tabDropController.RootGrid_DragOver(sender, e);

    private async void RootGrid_Drop(object sender, DragEventArgs e)
        => await _tabDropController.RootGrid_DropAsync(sender, e);

    private void RootGrid_DragLeave(object sender, DragEventArgs e)
        => _tabDropController.RootGrid_DragLeave(sender, e);

    private void TabList_DragLeave(object sender, DragEventArgs e)
        => _tabDropController.TabList_DragLeave(sender, e);

    private async void TabList_Drop(object sender, DragEventArgs e)
        => await _tabDropController.TabList_DropAsync(sender, e);

    private void FolderTabHeader_DragOver(object sender, DragEventArgs e)
        => _ = TryHandleFolderTabFileDragOver(e);

    private void FolderTabHeader_DragLeave(object sender, DragEventArgs e)
        => _folderTabFileDropController.HeaderDragLeave(e);

    private async void FolderTabHeader_Drop(object sender, DragEventArgs e)
        => _ = await TryHandleFolderTabFileDropAsync(e);

    private bool TryHandleFolderTabFileDragOver(DragEventArgs e)
        => _folderTabFileDropController.TryHandleDragOver(e);

    private Task<bool> TryHandleFolderTabFileDropAsync(DragEventArgs e)
        => _folderTabFileDropController.TryHandleDropAsync(e);

    private void ResetFolderTabFileDropHover()
        => _folderTabFileDropController.ResetHover();

    private static bool TryGetFolderFileDropPaths(IDataObject dataObject, out string[] paths)
    {
        paths = [];
        try
        {
            if (!dataObject.GetDataPresent(DataFormats.FileDrop, autoConvert: true)
                || dataObject.GetData(DataFormats.FileDrop, autoConvert: true) is not string[] fileDropPaths)
            {
                return false;
            }

            paths = fileDropPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return paths.Length > 0;
        }
        catch (Exception ex) when (
            ex is COMException
                or System.Runtime.InteropServices.ExternalException
                or InvalidOperationException)
        {
            paths = [];
            return false;
        }
    }

    private void WebHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateHostedWebViewSizes();
        UpdateFloatingMediaAfterHostSizeChanged();
        UpdateYouTubeControlPopupPlacement();
        PositionPageFindPopup();
    }

    private void WebHost_LayoutUpdated(object? sender, EventArgs e)
    {
        UpdateFloatingMediaAfterHostLayoutUpdated();
    }

    private void TabList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_tabs.Count < 2 || e.Delta == 0)
        {
            return;
        }

        MoveTabSelection(e.Delta < 0 ? 1 : -1);
        e.Handled = true;
    }

    private void TabItem_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is BrowserTab tab)
        {
            _pendingTabHoverPrewarm = tab;
            _pendingTabHoverElement = element;
            _tabHoverPrewarmTimer.Stop();
            _tabHoverPrewarmTimer.Start();
        }
    }

    private void TabItem_MouseLeave(object sender, MouseEventArgs e)
    {
        if (ReferenceEquals(sender, _pendingTabHoverElement))
        {
            _tabHoverPrewarmTimer.Stop();
            _pendingTabHoverPrewarm = null;
            _pendingTabHoverElement = null;
        }
    }

    private async void GoButton_Click(object sender, RoutedEventArgs e)
    {
        await NavigateFromAddressBarAsync(AddressBox.Text);
    }

    private void ActionsButton_Click(object sender, RoutedEventArgs e)
    {
        RebuildBookmarkHierarchyMenu();
        OpenButtonContextMenu(ActionsButton);
    }

    private void ToolsButton_Click(object sender, RoutedEventArgs e)
    {
        OpenButtonContextMenu(ToolsButton);
    }

    private async void UserGuideMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteRegisteredCommandAsync(BrowserCommandProvider.OpenUserGuideActionId);
    }

    private void CopyCurrentPageLinkMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string tag
            || !Enum.TryParse<LinkCopyFormat>(tag, ignoreCase: true, out var format))
        {
            SetStatus("コピー形式を判定できません", autoClear: true);
            return;
        }

        CopyCurrentPageLink(format);
    }

    private void CopyAllTabsMarkdownMenuItem_Click(object sender, RoutedEventArgs e)
    {
        CopyAllTabsMarkdown();
    }

    private void SetLinkCopyStatus(LinkCopyResult result)
    {
        SetStatus(result.Message, autoClear: true);
    }

    private void CopyCurrentPageLink(LinkCopyFormat format)
    {
        var target = _tabExportService.CreateLinkCopyTarget(ActiveTab);
        if (target is null)
        {
            SetStatus("コピーできる現在ページ URL がありません", autoClear: true);
            return;
        }

        SetLinkCopyStatus(_linkCopyService.Copy(target, format));
    }

    private void CopyAllTabsMarkdown()
    {
        var items = _tabExportService.ExportTabs(_tabs);
        SetLinkCopyStatus(_linkCopyService.CopyAllTabsMarkdown(items));
    }

    private void DownloadsButton_Click(object sender, RoutedEventArgs e)
    {
        _downloadShelfController?.Toggle();
    }

    private void QueueDownloadUiUpdate(Action action)
    {
        if (_isClosingWindow || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_isClosingWindow)
                {
                    return;
                }

                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    SetStatus("ダウンロード表示更新に失敗しました: " + TrimDialogText(ex.Message, 160), autoClear: true);
                }
            }), DispatcherPriority.Background);
        }
        catch
        {
            // 終了中の Dispatcher 競合では通知を破棄する。
        }
    }

    private void OpenDownloadShelfForStart() => _downloadShelfController?.OpenForStart();

    private void OpenDownloadShelfForCompletion() => _downloadShelfController?.OpenForCompletion();

    private void DownloadSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _downloadShelfController?.Refresh();
    }

    private void DownloadKindFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _downloadShelfController?.Refresh();
    }

    private void DownloadCancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetDownloadItem(sender, out var item))
        {
            _downloadService.Cancel(item);
            UpdateDownloadsButtonState();

            SetStatus("ダウンロードをキャンセルしました", autoClear: true);

        }
    }

    private void DownloadSaveAndContinueButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetDownloadItem(sender, out var item)
            || !item.CanSaveAndContinue)
        {
            return;
        }

        if (!_downloadService.TryFinalizeExternalDownloadSegment(item))
        {
            SetStatus("ライブダウンロードの保存処理を開始できませんでした", autoClear: true);
            return;
        }

        UpdateDownloadsButtonState();
        SetStatus("ここまでのファイルを確定後、現在のライブ位置から継続します", autoClear: true);
    }

    private void DownloadPauseResumeButton_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetDownloadItem(sender, out var item))
        {
            _downloadService.PauseOrResume(item);
            UpdateDownloadsButtonState();
        }
    }

    private void DownloadOpenButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetDownloadItem(sender, out var item))
        {
            return;
        }

        if (!File.Exists(item.FilePath))
        {
            SetStatus("ダウンロード済みファイルが見つかりません", autoClear: true);
            return;
        }

        var currentSafety = _downloadPolicy.InspectFileName(Path.GetFileName(item.FilePath));
        if (currentSafety.ShouldWarn)
        {
            item.IsDangerous = true;
            item.SafetyWarning = currentSafety.WarningText;
        }

        if (item.IsDangerous && !ConfirmOpenDangerousDownload(item))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(item.FilePath)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            SetStatus("ファイルを開けません: " + ex.Message, autoClear: true);
        }
    }

    private void DownloadOpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetDownloadItem(sender, out var item))
        {
            return;
        }

        try
        {
            var folderPath = _downloadPathResolver.ResolveExistingDirectory(item.FilePath);
            if (!string.IsNullOrWhiteSpace(folderPath))
            {
                OpenShellFolderInTab(folderPath);
                return;
            }

            SetStatus("保存先フォルダーが見つかりません", autoClear: true);
        }
        catch (Exception ex)
        {
            SetStatus("フォルダーを開けません: " + ex.Message, autoClear: true);
        }
    }

    private void DownloadOpenFolderInExplorerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetDownloadItem(sender, out var item))
        {
            return;
        }

        try
        {
            if (File.Exists(item.FilePath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.FilePath}\"")
                {
                    UseShellExecute = true
                });
                return;
            }

            var folderPath = _downloadPathResolver.ResolveExistingDirectory(item.FilePath);
            if (!string.IsNullOrWhiteSpace(folderPath))
            {
                Process.Start(new ProcessStartInfo(folderPath)
                {
                    UseShellExecute = true
                });
                return;
            }

            SetStatus("保存先フォルダーが見つかりません", autoClear: true);
        }
        catch (Exception ex)
        {
            SetStatus("フォルダーを開けません: " + ex.Message, autoClear: true);
        }
    }

    private async void DownloadCopyLinkButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetDownloadItem(sender, out var item)
            || string.IsNullOrWhiteSpace(item.SourceUrl))
        {
            return;
        }

        var result = await _clipboardWriteService.SetTextAsync(item.SourceUrl);
        SetStatus(
            result.IsSuccess
                ? "ダウンロード元 URL をコピーしました"
                : "クリップボードを使用できませんでした。少し待ってから再試行してください。",
            autoClear: true);
    }

    private async void DownloadDetailButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetDownloadItem(sender, out var item))
        {
            return;
        }

        var dialog = new DownloadDetailWindow(item, RetryDownloadItem)
        {
            Owner = this
        };
        await ModelessWindowService.ShowAsync(dialog);
    }

    private void DownloadRetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetDownloadItem(sender, out var item))
        {
            return;
        }

        RetryDownloadItem(item);
    }

    private void RetryDownloadItem(DownloadItem item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.SourceUrl))
        {
            return;
        }

        if (item.IsYtDlpDownload)
        {
            SetStatus("この履歴は公開版では再試行できません", autoClear: true);
            return;
        }

        OpenTab(item.SourceUrl, activate: true, title: item.FileName);
        SetStatus("再試行用にダウンロード元を開きました", autoClear: true);
    }

    private void DownloadRemoveHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetDownloadItem(sender, out var item))
        {
            return;
        }

        if (!item.CanRemoveHistory)
        {
            SetStatus("進行中のダウンロード履歴は削除できません", autoClear: true);
            return;
        }

        _downloadService.RemoveHistory(item);
        _downloadShelfController?.Refresh();
        UpdateDownloadsButtonState();
        SetStatus("ダウンロード履歴を削除しました。ファイルは削除していません。", autoClear: true);
    }

    private void DownloadDeleteFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetDownloadItem(sender, out var item))
        {
            return;
        }

        if (!File.Exists(item.FilePath))
        {
            SetStatus("削除対象のファイルが見つかりません", autoClear: true);
            return;
        }

        var result = MessageBox.Show(
            this,
            $"ローカルファイルだけを削除します。履歴は残します。\n\n{item.FilePath}",
            "ダウンロードファイルを削除",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            File.Delete(item.FilePath);
            SetStatus("ダウンロードファイルを削除しました。履歴は残しています。", autoClear: true);
        }
        catch (Exception ex)
        {
            SetStatus("ファイルを削除できません: " + ex.Message, autoClear: true);
        }
    }

    private static bool TryGetDownloadItem(object sender, out DownloadItem item)
    {
        item = null!;
        if (sender is FrameworkElement { DataContext: DownloadItem dataContext })
        {
            item = dataContext;
            return true;
        }

        return false;
    }

    private static void OpenButtonContextMenu(Button button)
    {
        if (button.ContextMenu is null)
        {
            return;
        }

        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.Placement = PlacementMode.Bottom;
        button.ContextMenu.IsOpen = true;
    }

    private void RebuildBookmarkHierarchyMenu() => _bookmarkMenuController.Rebuild();

    private void AddressBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        ShowTopToolbar();
        _addressBarController.BeginEdit();
    }

    private void AddressBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        CloseAddressSuggestions();
        RestoreTemporaryAddressBoxVisibility();
        QueueTopToolbarAutoHide();
    }

    private void AddressBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => _addressBarController.HandleMouseLeftButtonDown(e);

    private void AddressBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => _addressBarController.HandleMouseLeftButtonUp();

    private void AddressBox_PreviewMouseMove(object sender, MouseEventArgs e)
        => _addressBarController.HandleMouseMove(e);

    private async void AddressBox_KeyDown(object sender, KeyEventArgs e)
        => await _addressBarController.HandleKeyDownAsync(e);

    private void AddressBoxContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        ShowTopToolbar();
        _addressBarController.OpenContextMenu(TryReadAddressPasteNavigationText);
    }

    private void AddressBoxContextMenu_Closed(object sender, RoutedEventArgs e)
    {
        _addressBarController.CloseContextMenu();
        QueueTopToolbarAutoHide();
    }

    private async void AddressPasteAndNavigateMenuItem_Click(object sender, RoutedEventArgs e)
        => await _addressBarController.PasteAndNavigateAsync();

    private static string? TryReadAddressPasteNavigationText()
    {
        try
        {
            return Clipboard.ContainsText(TextDataFormat.UnicodeText)
                ? AddressService.NormalizePasteInput(Clipboard.GetText(TextDataFormat.UnicodeText))
                : null;
        }
        catch (ExternalException)
        {
            return null;
        }
    }

    private async void AddressBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // XAML構築中の初期通知では、まだ入力・候補表示を開始しない。
        if (_addressBarController is { } controller)
        {
            await controller.HandleTextChangedAsync();
        }
    }

    private async void BackButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteRegisteredCommandAsync(BrowserCommandProvider.BackActionId);
    }

    private async void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteRegisteredCommandAsync(BrowserCommandProvider.ForwardActionId);
    }

    private async void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteRegisteredCommandAsync(BrowserCommandProvider.ReloadActionId);
    }

    private async void YouTubeQualityButton_Click(object sender, RoutedEventArgs e)
    {
        await ApplyYouTubeBestQualityForActiveTabAsync(showStatus: true);
    }

    private async void YouTubeSpeedDownButton_Click(object sender, RoutedEventArgs e)
    {
        await ChangeYouTubePlaybackSpeedAsync(-YouTubeSpeedStep);
    }

    private async void YouTubeSpeedResetButton_Click(object sender, RoutedEventArgs e)
    {
        await ToggleYouTubePlaybackSpeedAsync();
    }

    private async void YouTubeSpeedUpButton_Click(object sender, RoutedEventArgs e)
    {
        await ChangeYouTubePlaybackSpeedAsync(YouTubeSpeedStep);
    }

    private async void YouTubeLoopButton_Click(object sender, RoutedEventArgs e)
    {
        await ToggleYouTubePlaybackLoopAsync();
    }

    private async void SplitLeftYouTubeSpeedDownButton_Click(object sender, RoutedEventArgs e)
    {
        await ChangeYouTubePlaybackSpeedForPaneAsync(SplitViewPane.Left, -YouTubeSpeedStep);
    }

    private async void SplitLeftYouTubeSpeedResetButton_Click(object sender, RoutedEventArgs e)
    {
        await ToggleYouTubePlaybackSpeedForPaneAsync(SplitViewPane.Left);
    }

    private async void SplitLeftYouTubeSpeedUpButton_Click(object sender, RoutedEventArgs e)
    {
        await ChangeYouTubePlaybackSpeedForPaneAsync(SplitViewPane.Left, YouTubeSpeedStep);
    }

    private async void SplitLeftYouTubeLoopButton_Click(object sender, RoutedEventArgs e)
    {
        await ToggleYouTubePlaybackLoopForPaneAsync(SplitViewPane.Left);
    }

    private async void SplitRightYouTubeSpeedDownButton_Click(object sender, RoutedEventArgs e)
    {
        await ChangeYouTubePlaybackSpeedForPaneAsync(SplitViewPane.Right, -YouTubeSpeedStep);
    }

    private async void SplitRightYouTubeSpeedResetButton_Click(object sender, RoutedEventArgs e)
    {
        await ToggleYouTubePlaybackSpeedForPaneAsync(SplitViewPane.Right);
    }

    private async void SplitRightYouTubeSpeedUpButton_Click(object sender, RoutedEventArgs e)
    {
        await ChangeYouTubePlaybackSpeedForPaneAsync(SplitViewPane.Right, YouTubeSpeedStep);
    }

    private async void SplitRightYouTubeLoopButton_Click(object sender, RoutedEventArgs e)
    {
        await ToggleYouTubePlaybackLoopForPaneAsync(SplitViewPane.Right);
    }

    private async void YouTubeFullscreenButton_Click(object sender, RoutedEventArgs e)
    {
        await EnterYouTubeFullscreenAsync();
    }

    private async void AddExtensionButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_settings.EnableBrowserExtensions)
        {
            SetStatus("拡張機能は設定で無効です", autoClear: true);
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "manifest.json を含む拡張機能フォルダを選択",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await AddExtensionFolderAsync(dialog.FolderName);
    }

    private async void ImportExtensionButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_settings.EnableBrowserExtensions)
        {
            SetStatus("拡張機能は設定で無効です", autoClear: true);
            return;
        }

        var dialog = new ExtensionImportWindow
        {
            Owner = this
        };

        await ModelessWindowService.ShowAsync(dialog);
        if (!dialog.IsAccepted || string.IsNullOrWhiteSpace(dialog.SelectedExtensionFolder))
        {
            return;
        }

        await AddExtensionFolderAsync(dialog.SelectedExtensionFolder);
    }

    private async Task AddExtensionFolderAsync(string extensionFolder)
    {
        if (!File.Exists(Path.Combine(extensionFolder, "manifest.json")))
        {
            SetStatus("manifest.json があるフォルダを選択してください", autoClear: true);
            return;
        }

        var tab = ActiveTab is { IsNativeContentTab: false } activeTab
            ? activeTab
            : AddNewTab(activate: true);
        await EnsureTabLoadedAsync(tab, tab.Url);

        if (GetTabWebView(tab)?.CoreWebView2 is not { } core)
        {
            SetStatus("WebView2 の準備ができていません", autoClear: true);
            return;
        }

        try
        {
            var extension = await core.Profile.AddBrowserExtensionAsync(extensionFolder);
            await RefreshExtensionToolbarAsync(core.Profile);
            SetStatus($"拡張機能を追加しました: {extension.Name}", autoClear: true);
        }
        catch (Exception ex)
        {
            SetStatus("拡張機能を追加できません: " + ex.Message, autoClear: true);
        }
    }

    private async void ManageExtensionsButton_Click(object sender, RoutedEventArgs e)
    {
        await OpenExtensionManagerAsync();
    }

    private async Task OpenExtensionManagerAsync()
    {
        if (!_settings.EnableBrowserExtensions)
        {
            SetStatus("拡張機能は設定で無効です", autoClear: true);
            return;
        }

        var tab = ActiveTab is { IsNativeContentTab: false } activeTab
            ? activeTab
            : AddNewTab(activate: true);
        await EnsureTabLoadedAsync(tab, tab.Url);

        if (GetTabWebView(tab)?.CoreWebView2 is not { } core)
        {
            SetStatus("WebView2 の準備ができていません", autoClear: true);
            return;
        }

        var dialog = new ExtensionManagerWindow(core.Profile, OpenExtensionPage)
        {
            Owner = this
        };
        var wasShown = await ModelessWindowService.ShowAsync(dialog);
        if (!wasShown || _isClosingWindow)
        {
            return;
        }

        await RefreshExtensionToolbarAsync(core.Profile);
    }

    private async void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteRegisteredCommandAsync(
            BrowserCommandProvider.RestoreClosedTabActionId);
    }

    private async void NewTabButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteRegisteredCommandAsync(BrowserCommandProvider.NewTabActionId);
    }

    private async void AddBookmarkButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteRegisteredCommandAsync(BrowserCommandProvider.AddBookmarkActionId);
    }

    private async void BookmarkListButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteRegisteredCommandAsync(BrowserCommandProvider.ShowBookmarksActionId);
    }

    private async void WorkspaceOSButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteRegisteredCommandAsync(FeatureCommandProvider.OpenWorkspaceActionId);
    }

    private void ChatGptReadabilityButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleChatGptReadabilityPanel();
    }

    private void ToggleChatGptReadabilityPanel()
    {
        OpenChatGptReadabilityPanel(forceOpen: false);
    }

    private void OpenChatGptReadabilityPanel(bool forceOpen)
    {
        if (!IsActiveChatGptTab())
        {
            ChatGptReadabilityPopup.IsOpen = false;
            SetStatus("ChatGPT ページでのみ使えます", autoClear: true);
            return;
        }

        SyncChatGptReadabilityPanel();
        ChatGptReadabilityPopup.IsOpen = forceOpen || !ChatGptReadabilityPopup.IsOpen;
    }

    private void ChatGptReadabilityOption_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SaveChatGptReadabilitySettingsFromPanel();
    }

    private void ChatGptReadabilityCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        SaveChatGptReadabilitySettingsFromPanel();
    }

    private void ChatGptReadabilityResetButton_Click(object sender, RoutedEventArgs e)
    {
        _chatGptReadabilityPanelController.Reset();
    }

    private async void GestureSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await OpenMouseGestureSettingsWindowAsync();
    }

    private async Task OpenMouseGestureSettingsWindowAsync(string? targetCommand = null)
    {
        if (ModelessWindowService.TryGetOwned<MouseGestureSettingsWindow>(this, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            existing.Activate();
            existing.FocusCommand(targetCommand);
            return;
        }

        var dialog = new MouseGestureSettingsWindow(
            _settings,
            targetCommand,
            _commandRegistry.CreateGestureCommandOptions())
        {
            Owner = this
        };
        await ModelessWindowService.ShowAsync(dialog);

        if (!dialog.IsAccepted || _isClosingWindow)
        {
            return;
        }

        var result = dialog.ResultSettings;
        await ApplyMouseGestureSettingsToOpenWindowsAsync(result);
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteRegisteredCommandAsync(BrowserCommandProvider.OpenSettingsActionId);
    }

    private async Task OpenSettingsDialogAsync()
    {
        if (ModelessWindowService.TryGetOwned<SettingsWindow>(this, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            existing.Activate();
            return;
        }

        var dialog = new SettingsWindow(_settings, GetInUseContainerProfileIdsForSettings())
        {
            Owner = this
        };
        dialog.SetGestureCommandOptions(_commandRegistry.CreateGestureCommandOptions());
        await ModelessWindowService.ShowAsync(dialog);

        if (_isClosingWindow)
        {
            return;
        }

        if (dialog.ContainerProfilesChanged)
        {
            foreach (var window in GetSettingsSynchronizationTargets())
            {
                window.ReloadContainerProfiles();
                window.QueueSessionSave();
                window.QueueWorkspaceOpenTabsRefresh();
            }
        }

        if (!dialog.IsAccepted)
        {
            return;
        }

        await ApplySettingsToOpenWindowsAsync(dialog.ResultSettings);
    }

    private async void CloseTabButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BrowserTab tab)
        {
            await ExecuteRegisteredCommandAsync(
                BrowserCommandProvider.CloseTabActionId,
                tab);
        }
    }

    private async void MuteTabButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BrowserTab tab)
        {
            await ExecuteRegisteredCommandAsync(
                BrowserCommandProvider.ToggleTabMuteActionId,
                tab);
        }
    }

    private void MuteTabButton_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button button || button.DataContext is not BrowserTab tab)
        {
            return;
        }

        if (!IsTabAudioBoostEligible(tab))
        {
            SetStatus("音量ブーストは通常のWebタブでのみ利用できます", autoClear: true);
            return;
        }

        if (GetTabWebView(tab)?.CoreWebView2 is null)
        {
            SetStatus("このタブはまだ音量ブーストを操作できません", autoClear: true);
            return;
        }

        _tabAudioBoostPopupController.Show(tab, button);
    }

    private async void TabAudioBoostButton_Click(object sender, RoutedEventArgs e)
        => await _tabAudioBoostPopupController.HandleButtonClickAsync(sender, e);

    private void TabAudioBoostPopup_Closed(object sender, EventArgs e)
        => _tabAudioBoostPopupController.OnClosed();

    Task<bool> ITabAudioBoostPopupHost.SetBoostAsync(BrowserTab tab, int audioBoostDecibels)
        => SetTabAudioBoostAsync(tab, audioBoostDecibels);

    void ITabAudioBoostPopupHost.QueueSessionSave() => QueueSessionSave();
    void ITabAudioBoostPopupHost.SetStatus(string message) => SetStatus(message, autoClear: true);

    private List<MainWindow> GetSettingsSynchronizationTargets()
    {
        var windows = GetOpenMainWindows()
            .Where(static window => !window._isClosingWindow)
            .ToList();
        if (!windows.Contains(this))
        {
            windows.Add(this);
        }

        return windows;
    }

    internal void UpdateWorkspaceNoteEditorWindowPlacement(
        Guid? noteId,
        WorkspaceNoteEditorWindowPlacementSettings placement)
    {
        if (WorkspaceNoteEditorWindowPlacementProfile.Normalize(placement) is { } normalized)
        {
            _settings.WorkspaceNoteEditorWindowPlacement = normalized;
            _settings.WorkspaceNoteEditorWindowPlacements ??= [];
            if (noteId is { } itemId && itemId != Guid.Empty)
            {
                _settings.WorkspaceNoteEditorWindowPlacements[itemId] = normalized;
            }
        }
    }

    private async Task ApplyMouseGestureSettingsToOpenWindowsAsync(AppSettings result)
    {
        var windows = GetSettingsSynchronizationTargets();
        foreach (var window in windows)
        {
            window._settings.GestureStartThreshold = result.GestureStartThreshold;
            window._settings.GestureDirectionThreshold = result.GestureDirectionThreshold;
            window._settings.PreserveContextMenuOnShortRightClick =
                result.PreserveContextMenuOnShortRightClick;
            window._settings.GestureMappings = GestureSettingsCatalog.NormalizeMappings(
                result.GestureMappings);
        }

        SettingsService.Save(_settings);
        foreach (var window in windows)
        {
            window.RefreshWorkspaceGestureSettings();
            await window.RefreshGestureScriptOnLoadedTabsAsync();
            await window.RefreshMediaWindowGestureSettingsAsync();
        }

        SetStatus("マウスジェスチャー設定をすべてのウィンドウへ適用しました", autoClear: true);
    }

    private async Task ApplySettingsToOpenWindowsAsync(AppSettings result)
    {
        var windows = GetSettingsSynchronizationTargets();
        foreach (var window in windows.Where(window => !ReferenceEquals(window, this)))
        {
            await window.ApplySettingsAsync(
                result,
                persistSettings: false,
                showStatus: false);
        }

        await ApplySettingsAsync(result, persistSettings: true, showStatus: true);
    }

    private async Task ApplySettingsAsync(
        AppSettings result,
        bool persistSettings,
        bool showStatus)
    {
        var extensionSettingChanged = _settings.EnableBrowserExtensions != result.EnableBrowserExtensions;
        var chatGptReadabilitySettings = ChatGptReadabilityProfile.Normalize(result.ChatGptReadability);
        var chatGptReadabilityChanged = _settings.ChatGptReadability.IsEnabled != chatGptReadabilitySettings.IsEnabled;
        var fiveChLiveReadabilitySettings = FiveChLiveReadabilityProfile.Normalize(result.FiveChLiveReadability);

        _settings.SearchEngine = result.SearchEngine;
        _settings.SearchUrlTemplate = result.SearchUrlTemplate;
        _settings.SearchAliases = SearchAliasSettingsProfile.Normalize(result.SearchAliases);
        _settings.EnableFocusedViewingCpuSaver = result.EnableFocusedViewingCpuSaver;
        _settings.FocusedViewingCpuSaverDelaySeconds = result.FocusedViewingCpuSaverDelaySeconds;
        _settings.EnableInactiveTabMemorySaver = result.EnableInactiveTabMemorySaver;
        _settings.EnableLinkHoverPreview = result.EnableLinkHoverPreview;
        if (!_settings.EnableLinkHoverPreview)
        {
            HideLinkPreview(stopPreviewPlayback: true);
        }

        RestartTabMemoryManagementTimer();
        _settings.ClipboardHistory = ClipboardHistorySettingsProfile.Normalize(result.ClipboardHistory);
        ApplyClipboardHistorySettings();
        _settings.BrowsingHistory = BrowsingHistorySettingsProfile.Normalize(result.BrowsingHistory);
        _settings.SitePrivacy = SitePrivacySettingsProfile.Normalize(result.SitePrivacy);
        App.BrowsingHistory.Configure(_settings.BrowsingHistory);
        _settings.EnableBrowserExtensions = result.EnableBrowserExtensions;

        _settings.PreservedCompatibilitySettings = PublicSettingsCompatibility.Clone(result.PreservedCompatibilitySettings);

        _settings.EnableXReadableMode = result.EnableXReadableMode;
        _settings.ChatGptReadability = chatGptReadabilitySettings;
        _settings.FiveChLiveReadability = fiveChLiveReadabilitySettings;

        _settings.Downloads = DownloadSettingsProfile.Normalize(result.Downloads);
        _settings.TabLayout = TabLayoutSettingsProfile.Normalize(result.TabLayout);
        _settings.FolderTabViewMode = FolderTabViewMode.Normalize(result.FolderTabViewMode);
        _settings.FolderTabPinnedPlaces = FolderTabPlacesService.NormalizePinnedPlaces(
            result.FolderTabPinnedPlaces);
        _settings.ShowHiddenFolderTabItems = result.ShowHiddenFolderTabItems;
        foreach (var folderTabView in _tabs
            .Select(tab => GetTabHostedContent(tab))
            .OfType<Views.FolderTabView>())
        {
            folderTabView.UpdateShowHiddenItems(_settings.ShowHiddenFolderTabItems);
        }
        _settings.QuickLook = QuickLookSettingsProfile.Normalize(result.QuickLook);
        await ApplyFloatingMediaSettingsAsync(result);
        CommandPaletteQuickLookHost.Configure(_settings.QuickLook);
        _settings.GestureStartThreshold = result.GestureStartThreshold;
        _settings.GestureDirectionThreshold = result.GestureDirectionThreshold;
        _settings.PreserveContextMenuOnShortRightClick = result.PreserveContextMenuOnShortRightClick;
        _settings.GestureMappings = GestureSettingsCatalog.NormalizeMappings(result.GestureMappings);
        _settings.ToolbarVisibility = CloneToolbarVisibility(result.ToolbarVisibility);

        _addressService = new AddressService(_settings);
        if (IsCommandPaletteOpen)
        {
            RefreshCommandPaletteItems();
        }

        ApplyToolbarVisibility();
        ApplyTabLayoutSettings();
        _downloadService.ApplyHistoryRetentionLimit(_settings.Downloads.HistoryRetentionLimit);
        SyncXReadableModeMenuItem();
        if (persistSettings)
        {
            SettingsService.Save(_settings);
        }

        RestartFocusedViewingCpuSaverTimer();
        RefreshWorkspaceGestureSettings();
        await RefreshGestureScriptOnLoadedTabsAsync();
        await RefreshMediaWindowGestureSettingsAsync();
        ApplyXReadableModeToLoadedTabs();
        ApplyFiveChLiveReadabilityToLoadedTabs();
        if (chatGptReadabilityChanged)
        {
            SyncChatGptReadabilityPanel();
            ApplyChatGptReadabilityToLoadedTabs();
        }

        if (showStatus)
        {
            var message = extensionSettingChanged && _webViewEnvironmentService.HasCreatedEnvironment
                ? "設定をすべてのウィンドウへ適用しました。拡張機能の有効/無効は次回起動後に完全反映されます"
                : "設定をすべてのウィンドウへ適用しました";
            SetStatus(message, autoClear: true);
        }
    }

    private void XReadableModeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _settings.EnableXReadableMode = XReadableModeMenuItem.IsChecked;
        SettingsService.Save(_settings);
        ApplyXReadableModeToLoadedTabs();

        SetStatus(
            _settings.EnableXReadableMode
                ? "X 整形モードを有効にしました"
                : "X 整形モードを無効にしました",
            autoClear: true);
    }

    private async Task NavigateFromAddressBarAsync(string input)
    {
        NoteExplorationActivity();
        if (GetTabHostedContent(ActiveTab) is Views.FolderTabView folderTabView
            && Directory.Exists(input.Trim()))
        {
            await folderTabView.NavigateToAsync(input);
            return;
        }

        var shouldOpenSearchInNewTab = _addressService.IsSearchInput(input);
        var target = NormalizeYouTubeShortsUrl(_addressService.Resolve(input));
        var activeTab = ActiveTab is { IsNativeContentTab: false } currentTab
            ? currentTab
            : null;

        if (shouldOpenSearchInNewTab)
        {
            OpenTab(target, activate: true, containerProfileId: activeTab?.ContainerProfileId);
            SetStatus("検索を新しいタブで開きました", autoClear: true);
            return;
        }

        var tab = activeTab ?? AddNewTab(activate: true);

        if (target == "about:blank")
        {
            tab.Url = null;
            tab.Title = null;
            ShowBlankView();
            QueueSessionSave();
            return;
        }

        tab.Url = target;
        ResetWebViewLoadFailureForRetry(tab);
        await EnsureTabLoadedAsync(tab, null);
        NavigateCore(tab, target);
        QueueSessionSave();
    }

    private async void AddressSuggestionList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject) is not { DataContext: AddressSuggestionItem suggestion })
        {
            return;
        }

        e.Handled = true;
        await OpenAddressSuggestionAsync(suggestion, BrowsingHistoryOpenMode.CurrentTab);
    }

    private async Task OpenAddressSuggestionAsync(
        AddressSuggestionItem suggestion,
        BrowsingHistoryOpenMode mode)
    {
        CloseAddressSuggestions();
        switch (suggestion.Kind)
        {
            case AddressSuggestionKind.OpenTab when mode == BrowsingHistoryOpenMode.CurrentTab:
                await ActivateAddressSuggestionTabAsync(suggestion.SourceId);
                return;
            case AddressSuggestionKind.OpenTab:
                await OpenBrowsingHistoryEntryAsync(suggestion.CreateHistoryEntry(), mode);
                return;
            case AddressSuggestionKind.Bookmark:
                var bookmark = _bookmarks.FirstOrDefault(item => item.Id == suggestion.SourceId);
                if (bookmark is null)
                {
                    SetStatus("ブックマークが見つかりません", autoClear: true);
                    return;
                }

                if (mode == BrowsingHistoryOpenMode.CurrentTab)
                {
                    await OpenBookmarkAsync(bookmark);
                    return;
                }

                await _bookmarkController.RecordOpenAsync(bookmark);
                await OpenBrowsingHistoryEntryAsync(
                    suggestion.CreateHistoryEntry(ActiveTab?.ContainerProfileId),
                    mode);
                return;
            case AddressSuggestionKind.BrowsingHistory when suggestion.HistoryEntry is { } historyEntry:
                await OpenBrowsingHistoryEntryAsync(historyEntry, mode);
                return;
        }
    }

    private async Task ActivateAddressSuggestionTabAsync(Guid tabId)
    {
        var target = GetOpenMainWindows()
            .Where(window => window._isPrivateWindow == _isPrivateWindow)
            .Select(window => new
            {
                Window = window,
                Tab = window._tabs.FirstOrDefault(tab => tab.Id == tabId)
            })
            .FirstOrDefault(static candidate => candidate.Tab is not null);
        if (target?.Tab is not { } tab)
        {
            SetStatus("移動先タブが見つかりません", autoClear: true);
            return;
        }

        var targetWindow = target.Window;
        if (targetWindow.WindowState == WindowState.Minimized)
        {
            targetWindow.WindowState = WindowState.Normal;
        }

        targetWindow.Activate();
        targetWindow.SelectTab(tab);
        await targetWindow.ActivateSelectedTabAsync();
        targetWindow.SetStatus("タブへ移動しました: " + tab.DisplayTitle, autoClear: true);
    }

    private List<AddressSuggestionCandidate> CreateAddressMemorySuggestionCandidates()
    {
        var candidates = new List<AddressSuggestionCandidate>();
        foreach (var window in GetOpenMainWindows().Where(window => window._isPrivateWindow == _isPrivateWindow))
        {
            foreach (var tab in window._tabs.Where(static tab => !tab.IsNativeContentTab))
            {
                if (ReferenceEquals(window, this) && tab.Id == ActiveTab?.Id)
                {
                    continue;
                }

                var url = GetCurrentPageUrl(tab) ?? tab.Url;
                if (string.IsNullOrWhiteSpace(url) || IsBlankUrl(url))
                {
                    continue;
                }

                var host = TryGetHost(url) ?? string.Empty;
                var location = ReferenceEquals(window, this) ? "このWindow" : "別Window";
                candidates.Add(new AddressSuggestionCandidate(
                    new AddressSuggestionSource(
                        AddressSuggestionKind.OpenTab,
                        $"tab:{tab.Id:D}",
                        url,
                        tab.DisplayTitle,
                        host,
                        string.IsNullOrWhiteSpace(host) ? location : $"{host} ・ {location}",
                        tab.ContainerProfileId,
                        LastUsedAt: tab.LastActivatedAt),
                    tab.Id));
            }
        }

        candidates.AddRange(_bookmarks
            .Where(static bookmark => !string.IsNullOrWhiteSpace(bookmark.Url))
            .Select(bookmark => new AddressSuggestionCandidate(
                new AddressSuggestionSource(
                    AddressSuggestionKind.Bookmark,
                    $"bookmark:{bookmark.Id:D}",
                    bookmark.Url,
                    string.IsNullOrWhiteSpace(bookmark.Title) ? bookmark.Url : bookmark.Title,
                    TryGetHost(bookmark.Url) ?? string.Empty,
                    string.IsNullOrWhiteSpace(bookmark.FolderPath)
                        ? TryGetHost(bookmark.Url) ?? string.Empty
                        : bookmark.FolderPath,
                    ContainerProfile.DefaultId,
                    bookmark.OpenCount,
                    bookmark.LastOpenedAt ?? bookmark.CreatedAt),
                bookmark.Id)));
        return candidates;
    }

    private void CloseAddressSuggestions() => _addressBarController.CloseAddressSuggestions();

    private void AddressSuggestionPopup_Closed(object? sender, EventArgs e)
        => _addressBarController?.HandlePopupClosed();

    private bool TryGetAddressBarDragUrl(out string url)
    {
        url = string.Empty;
        if (ActiveTab is not { IsNativeContentTab: false } tab)
        {
            return false;
        }

        var currentUrl = GetCurrentPageUrl(tab);
        if (string.IsNullOrWhiteSpace(currentUrl) || IsBlankUrl(currentUrl))
        {
            return false;
        }

        url = currentUrl.Trim();
        return true;
    }

    private DataObject CreateUrlDragData(string url, BrowserTab? sourceTab = null)
    {
        var data = new DataObject();
        data.SetData(DataFormats.Text, url);
        data.SetData(DataFormats.UnicodeText, url);
        data.SetData(DataFormats.StringFormat, url);
        data.SetData("URL", url);
        data.SetData("UniformResourceLocatorW", new MemoryStream(System.Text.Encoding.Unicode.GetBytes(url + "\0")));

        if ((sourceTab ?? ActiveTab) is { } tab)
        {
            data.SetData("text/x-moz-url", url + "\n" + GetCurrentPageTitle(tab, url));
        }

        return data;
    }

    private DataObject CreateSiteInformationUrlDragData(BrowserTab tab, string url)
    {
        var data = CreateUrlDragData(url, tab);
        data.SetData(
            SiteInformationUrlDragFormat,
            JsonSerializer.Serialize(new SiteInformationUrlDragSnapshot
            {
                SourceWindowId = _workspaceWindowId,
                SourceTabId = tab.Id,
                Url = url
            }));
        return data;
    }

    private void SetAddressText(string text) => _addressBarController.SetAddressText(text);

    bool IAddressBarHost.IsPrivateWindow => _isPrivateWindow;

    bool IAddressBarHost.IsBrowsingHistoryEnabled => _settings.BrowsingHistory.IsEnabled;

    bool IAddressBarHost.TryGetDragUrl(out string url) => TryGetAddressBarDragUrl(out url);

    void IAddressBarHost.StartUrlDrag(string url)
        => DragDrop.DoDragDrop(AddressBox, CreateUrlDragData(url), DragDropEffects.Copy);

    bool IAddressBarHost.IsFolderNavigation(string input)
        => GetTabHostedContent(ActiveTab) is Views.FolderTabView && Directory.Exists(input);

    bool IAddressBarHost.IsSearchInput(string input) => _addressService.IsSearchInput(input);

    Task IAddressBarHost.NavigateAsync(string input) => NavigateFromAddressBarAsync(input);

    Task IAddressBarHost.OpenSuggestionAsync(AddressSuggestionItem suggestion, BrowsingHistoryOpenMode mode)
        => OpenAddressSuggestionAsync(suggestion, mode);

    IReadOnlyList<AddressSuggestionCandidate> IAddressBarHost.CaptureMemoryCandidates()
        => CreateAddressMemorySuggestionCandidates();

    private SessionRestoreResult RestoreSession(SessionData session, bool restoreClosedTabs = true)
        => _sessionRestorationController.RestoreSession(session, restoreClosedTabs);

    private void RestoreWindowSet(SessionData session)
        => _sessionRestorationController.RestoreWindowSet(session);

    IList<BrowserTab> ISessionRestorationHost.Tabs => _tabs;
    BrowserTab? ISessionRestorationHost.ActiveTab => ActiveTab;
    Guid ISessionRestorationHost.WindowId => _workspaceWindowId;
    int ISessionRestorationHost.WindowIndex => _workspaceWindowIndex;
    void ISessionRestorationHost.MarkStartupPerformance(string stage, BrowserTab? tab, int? itemCount)
        => MarkStartupPerformance(stage, tab, itemCount);
    void ISessionRestorationHost.RestoreSharedClosedTabHistory(IEnumerable<ClosedTabState> closedTabs)
        => RestoreSharedClosedTabHistory(closedTabs);
    void ISessionRestorationHost.RestoreSupplementalClosedItemHistory(SessionData session)
        => RestoreSupplementalClosedItemHistory(session);
    void ISessionRestorationHost.RefreshClosedTabHistoryView() => RefreshClosedTabHistoryView();
    void ISessionRestorationHost.AppendSessionDiagnostic(string operation, Exception exception, string? detail)
        => AppendSessionDiagnostic(operation, exception, detail);
    BrowserTab ISessionRestorationHost.CreateFolderTab(SessionTabState tabState, BrowserTabState persistentState)
        => CreateFolderTab(tabState.Url ?? string.Empty, tabState, persistentState: persistentState);
    string? ISessionRestorationHost.NormalizeSessionTabUrl(string? url, string? title)
        => NormalizeSessionTabUrl(url, title);
    void ISessionRestorationHost.ApplyContainerProfileToTab(BrowserTab tab) => ApplyContainerProfileToTab(tab);
    void ISessionRestorationHost.SelectTab(BrowserTab tab) => SelectTab(tab);
    void ISessionRestorationHost.SetStatus(string message, bool autoClear) => SetStatus(message, autoClear);
    IReadOnlyCollection<SplitViewModel> ISessionRestorationHost.LoadSplitViews() => _splitViewStore.Load();
    void ISessionRestorationHost.AppendSplitViewRestoreDiagnostic(
        SplitViewRestoreResult result, int storedCandidateCount, int sessionCandidateCount,
        int ignoredSessionGroupCount, Guid? activeTabId)
        => AppendSplitViewRestoreDiagnostic(
            result, storedCandidateCount, sessionCandidateCount, ignoredSessionGroupCount, activeTabId);
    void ISessionRestorationHost.RefreshSplitViewTabVisualStates() => RefreshSplitViewTabVisualStates();
    void ISessionRestorationHost.PersistSplitViewState() => PersistSplitViewState();
    void ISessionRestorationHost.ApplySessionWindowIdentity(Guid windowId) => ApplySessionWindowIdentity(windowId);
    void ISessionRestorationHost.ApplyWindowPlacement(WindowPlacementSettings? placement)
        => ApplyWindowPlacement(placement ?? _settings.WindowPlacement);
    void ISessionRestorationHost.ApplyAlwaysOnTop(bool? isAlwaysOnTop)
        => ApplyAlwaysOnTop(isAlwaysOnTop ?? _settings.AlwaysOnTop);
    SessionData ISessionRestorationHost.CreateSessionData(SessionWindowState window) => CreateSessionData(window);
    TimeSpan ISessionRestorationHost.GetRestoredWindowStartupActivationDelay(int windowIndex, int windowCount)
        => GetRestoredWindowStartupActivationDelay(windowIndex, windowCount);

    private void AppendSplitViewRestoreDiagnostic(
        SplitViewRestoreResult result,
        int storedCandidateCount,
        int sessionCandidateCount,
        int ignoredSessionGroupCount,
        Guid? activeTabId)
    {
        AppendDiagnosticLogLine(
            "runner=split-restore",
            "operation=split-view-restore",
            $"window={_workspaceWindowIndex}",
            $"tabs={_tabs.Count}",
            $"activeTab={NormalizeSessionDiagnosticToken(activeTabId?.ToString("N") ?? string.Empty)}",
            $"storedCandidates={storedCandidateCount}",
            $"sessionCandidates={sessionCandidateCount}",
            $"ignoredSessionGroups={ignoredSessionGroupCount}",
            $"candidateCount={result.CandidateCount}",
            $"restoredCount={result.RestoredCount}",
            $"skippedCount={result.SkippedCount}",
            $"invalidCount={result.InvalidCount}",
            $"missingTabCount={result.MissingTabCount}",
            $"duplicateTabCount={result.DuplicateTabCount}",
            $"rejectedTabCount={result.RejectedTabCount}");
    }

    private static bool HasSessionWindows(SessionData session)
    {
        return session.Windows is { Count: > 0 };
    }

    private TimeSpan GetRestoredWindowStartupActivationDelay(int windowIndex, int windowCount)
    {
        return _startupTabActivationService.GetRestoredWindowStartupActivationDelay(
            windowIndex,
            windowCount,
            TimeSpan.FromMilliseconds(RestoredWindowStartupActivationDelayStepMilliseconds),
            TimeSpan.FromMilliseconds(RestoredWindowStartupActivationMaxDelayMilliseconds));
    }

    private void RestoreSharedClosedTabHistory(IEnumerable<ClosedTabState>? closedTabs)
        => _closedTabLifecycleController.ReplaceClosedTabHistory(closedTabs);

    private static List<MainWindow> GetOpenMainWindows()
    {
        return Application.Current.Windows
            .OfType<MainWindow>()
            .Where(static window => window.IsVisible)
            .OrderBy(static window => window._workspaceWindowIndex)
            .ToList();
    }

    private void ApplySessionWindowIdentity(Guid sessionWindowId)
    {
        if (sessionWindowId == Guid.Empty)
        {
            return;
        }

        var isAlreadyUsed = Application.Current.Windows
            .OfType<MainWindow>()
            .Any(window => !ReferenceEquals(window, this)
                && !window._isClosingWindow
                && window._sessionWindowId == sessionWindowId);
        if (!isAlreadyUsed)
        {
            _sessionWindowId = sessionWindowId;
        }
    }

    private static List<MainWindow> GetApplicationCloseMainWindows()
    {
        return Application.Current.Windows
            .OfType<MainWindow>()
            .Where(static window => window.IsVisible || window.IsHiddenFloatingMediaHostActive())
            .OrderBy(static window => window._workspaceWindowIndex)
            .ToList();
    }

    private static List<MainWindow> GetPersistableMainWindows()
    {
        return GetOpenMainWindows()
            .Where(static window => !window._isPrivateWindow)
            .ToList();
    }

    private IReadOnlyList<ContainerProfile> ReloadContainerProfiles(bool applyToTabs = true)
    {
        _containerProfiles = _containerProfileStore.Load();
        if (applyToTabs)
        {
            foreach (var tab in _tabs)
            {
                ApplyContainerProfileToTab(tab);
            }
        }

        return _containerProfiles;
    }

    private IReadOnlyCollection<string> GetInUseContainerProfileIdsForSettings()
    {
        var containerProfileIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ContainerProfile.DefaultId
        };

        foreach (var window in GetOpenMainWindows())
        {
            foreach (var tab in window._tabs)
            {
                containerProfileIds.Add(ContainerProfile.NormalizeId(tab.ContainerProfileId));
            }

            foreach (var closedTab in s_closedTabWindowHistoryStore.ClosedTabs)
            {
                containerProfileIds.Add(ContainerProfile.NormalizeId(closedTab.ContainerProfileId));
            }

            foreach (var containerProfileId in window._webViewEnvironmentService.GetCreatedContainerProfileIds())
            {
                containerProfileIds.Add(ContainerProfile.NormalizeId(containerProfileId));
            }
        }

        return containerProfileIds;
    }

    private ContainerProfile GetContainerProfile(string? containerProfileId)
    {
        var normalizedId = ContainerProfile.NormalizeId(containerProfileId);
        return _containerProfiles.FirstOrDefault(profile => string.Equals(
                profile.Id,
                normalizedId,
                StringComparison.OrdinalIgnoreCase))
            ?? _containerProfiles.FirstOrDefault(static profile => profile.IsDefault)
            ?? ContainerProfile.CreateDefault();
    }

    private void ApplyContainerProfileToTab(BrowserTab tab)
    {
        tab.ApplyContainerProfile(GetContainerProfile(tab.ContainerProfileId));
    }

    private BrowserTab AddNewTab(bool activate, string? containerProfileId = null)
    {
        NoteExplorationActivity();

        var tab = new BrowserTab
        {
            Url = NewTabUrl,
            Title = "新しいタブ",
            ContainerProfileId = ContainerProfile.NormalizeId(containerProfileId)
        };
        ApplyContainerProfileToTab(tab);
        _tabs.Add(tab);

        if (activate)
        {
            SelectTab(tab);
            ShowBlankView();
            SetAddressText(string.Empty);
            UpdateToolbarState();
            QueueSelectedTabActivation();
        }

        QueueSessionSave();
        QueueWorkspaceOpenTabsRefresh();
        return tab;
    }

    private BrowserTab OpenTab(
        string url,
        bool activate,
        string? title = null,
        int? insertionIndex = null,
        IReadOnlyList<string>? navigationHistory = null,
        int navigationHistoryIndex = -1,
        TabAudioPan audioPan = TabAudioPan.Stereo,
        bool isMuted = false,
        string? youTubePlaybackVideoId = null,
        double? youTubePlaybackPositionSeconds = null,
        double? youTubePlaybackSpeed = null,
        bool? youTubePlaybackIsPaused = null,
        bool queueBackgroundLoading = true,
        bool? youTubePlaybackIsLooping = null,
        bool? youTubePlaybackIsTheaterMode = null,
        string? containerProfileId = null,
        int audioBoostDecibels = 0)
    {
        NoteExplorationActivity();
        url = NormalizeYouTubeShortsUrl(url);

        var tab = new BrowserTab
        {
            Url = url,
            Title = title,
            AudioPan = audioPan,
            IsMuted = isMuted,
            AudioBoostDecibels = audioBoostDecibels,
            ContainerProfileId = ContainerProfile.NormalizeId(containerProfileId),
            YouTubePlaybackVideoId = youTubePlaybackVideoId,
            YouTubePlaybackPositionSeconds = youTubePlaybackPositionSeconds,
            YouTubePlaybackSpeed = youTubePlaybackSpeed,
            YouTubePlaybackIsPaused = youTubePlaybackIsPaused,
            YouTubePlaybackIsLooping = youTubePlaybackIsLooping,
            YouTubePlaybackIsTheaterMode = youTubePlaybackIsTheaterMode
        };
        ApplyContainerProfileToTab(tab);
        RestoreNavigationHistory(tab, navigationHistory, navigationHistoryIndex, NormalizeSessionTabUrl(url, title));

        if (insertionIndex is { } index)
        {
            _tabs.Insert(ClampNormalTabInsertionIndex(index), tab);
        }
        else
        {
            _tabs.Add(tab);
        }

        if (activate)
        {
            SelectTab(tab);
            QueueSelectedTabActivation();
        }

        if (queueBackgroundLoading)
        {
            QueueBackgroundTabLoading();
        }
        QueueSessionSave();
        QueueWorkspaceOpenTabsRefresh();
        return tab;
    }

    private BrowserTab OpenBackgroundTabNextTo(
        BrowserTab? sourceTab,
        string url,
        string? title = null,
        bool queueBackgroundLoading = true,
        string? containerProfileId = null)
    {
        var sourceIndex = sourceTab is not null
            ? _tabs.IndexOf(sourceTab)
            : -1;
        var activeIndex = ActiveTab is { } activeTab ? _tabs.IndexOf(activeTab) : -1;
        var insertionIndex = _tabOperationService.GetAdjacentInsertionIndex(_tabs.Count, sourceIndex, activeIndex);
        if (!string.IsNullOrWhiteSpace(sourceTab?.SplitViewId))
        {
            insertionIndex = _tabOperationService.GetInsertionIndexAfterGroup(
                _tabs.Count,
                insertionIndex,
                GetSplitViewGroupIndices(sourceTab.SplitViewId));
        }

        var openedTab = OpenTab(
            url,
            activate: false,
            title: title,
            insertionIndex: insertionIndex,
            containerProfileId: containerProfileId ?? sourceTab?.ContainerProfileId,
            queueBackgroundLoading: false);
        if (queueBackgroundLoading)
        {
            QueueUserRequestedBackgroundTabLoading(openedTab);
        }

        return openedTab;
    }

    private IEnumerable<int> GetSplitViewGroupIndices(string splitViewId)
    {
        for (var index = 0; index < _tabs.Count; index++)
        {
            if (string.Equals(_tabs[index].SplitViewId, splitViewId, StringComparison.OrdinalIgnoreCase))
            {
                yield return index;
            }
        }
    }

    private async Task OpenNewWindowRequestInForegroundTabAsync(
        BrowserTab sourceTab,
        CoreWebView2NewWindowRequestedEventArgs args)
    {
        NoteExplorationActivity();

        var sourceIndex = _tabs.IndexOf(sourceTab);
        var activeIndex = ActiveTab is { } activeTab ? _tabs.IndexOf(activeTab) : -1;
        var insertionIndex = _tabOperationService.GetAdjacentInsertionIndex(_tabs.Count, sourceIndex, activeIndex);
        var initialUrl = string.IsNullOrWhiteSpace(args.Uri) || IsBlankUrl(args.Uri)
            ? null
            : NormalizeYouTubeShortsUrl(args.Uri);
        var popupTab = new BrowserTab
        {
            Url = initialUrl,
            Title = "ログイン",
            ContainerProfileId = sourceTab.ContainerProfileId
        };
        ApplyContainerProfileToTab(popupTab);

        _tabs.Insert(ClampNormalTabInsertionIndex(insertionIndex), popupTab);
        _newWindowPopupTabIds.Add(popupTab.Id);
        SelectTab(popupTab);
        ShowBlankView();
        SetAddressText(initialUrl ?? string.Empty);
        UpdateToolbarState();
        QueueSessionSave();
        QueueWorkspaceOpenTabsRefresh();

        var popupWebView = CreateWebView(popupTab);
        ShowWebView(popupTab);
        await popupWebView.EnsureCoreWebView2Async(
            await EnsureWebViewEnvironmentAsync(sourceTab.ContainerProfileId));
        await ConfigureWebViewAsync(popupTab, addGestureScript: false);
        _ = s_tabRuntimeCoordinator.SetLoaded(popupTab, true);

        if (popupWebView.CoreWebView2 is not { } popupCore)
        {
            throw new InvalidOperationException("ログイン用ポップアップの WebView2 を初期化できませんでした。");
        }

        args.NewWindow = popupCore;
        SetStatus("ログイン用ポップアップを前面タブで開きました", autoClear: true);
    }

    private async Task OpenNewWindowRequestInForegroundTabWithDeferralAsync(
        BrowserTab sourceTab,
        CoreWebView2NewWindowRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            await OpenNewWindowRequestInForegroundTabAsync(sourceTab, args);
        }
        catch (Exception ex)
        {
            args.Handled = true;
            SetStatus("ログイン用ポップアップを開けません: " + ex.Message, autoClear: true);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void SelectTab(BrowserTab tab)
    {
        var previousTab = ActiveTab;
        if (previousTab is not null && !ReferenceEquals(previousTab, tab))
        {
            DismissLinkPreview();
            ResetNativeWebGestureForTab(previousTab);
            s_inputInteractionHub.CancelForTab(_workspaceWindowId, previousTab.Id);
        }

        _isChangingSelection = true;
        try
        {
            _tabStripController.SynchronizeSelection(tab);
        }
        finally
        {
            _isChangingSelection = false;
        }

        CancelInactiveYouTubeTabCheck(tab);
        if (previousTab is not null
            && !ReferenceEquals(previousTab, tab)
            && previousTab.IsLoading)
        {
            QueueInactiveYouTubeTabCheck(previousTab, stopStalledLoad: true);
        }

        if (!tab.IsWorkspaceTab)
        {
            _workspaceTabController.ClearReturnTab();
        }

        RecordRecentTabSelection(tab);
    }

    private void QueueSelectedTabActivation()
    {
        _windowTaskSupervisor.TryRun(
            "selected-tab-activation",
            cancellationToken => ActivateSelectedTabAsync(cancellationToken));
    }

    private Task ActivateSelectedTabAsync(CancellationToken cancellationToken = default)
        => _tabActivationController.ActivateSelectedTabAsync(cancellationToken);

    private bool CanUseTabRuntimeInCurrentWindow(BrowserTab tab)
    {
        return _webViewLifecycleService.CanUseRuntimeInWindow(
            tab,
            _workspaceWindowId,
            _tabs.Contains(tab));
    }

    private Task EnsureTabLoadedAsync(BrowserTab tab, string? initialUrl)
        => _tabActivationController.EnsureTabLoadedAsync(tab, initialUrl);

    BrowserTab? ITabActivationHost.ActiveTab => ActiveTab;
    CancellationToken ITabActivationHost.LifetimeToken => _windowTaskSupervisor.LifetimeToken;
    void ITabActivationHost.UpdateYouTubePlaybackStateTracking(BrowserTab? tab) => UpdateYouTubePlaybackStateTracking(tab);
    void ITabActivationHost.PrepareFolderTabContentTransition(BrowserTab? tab) => PrepareFolderTabContentTransition(tab);
    void ITabActivationHost.HideSplitViewForSingleTab(BrowserTab? tab) => HideSplitViewForSingleTab(tab);
    void ITabActivationHost.CancelInactiveFolderTabGestures(Guid? activeTabId) => CancelInactiveFolderTabGestures(activeTabId);
    void ITabActivationHost.CloseBookmarkPreview(bool clearNavigation) => CloseBookmarkPreview(clearNavigation: clearNavigation);
    void ITabActivationHost.ShowBlankView() => ShowBlankView();
    void ITabActivationHost.SetAddressText(string text) => SetAddressText(text);
    void ITabActivationHost.UpdateToolbarState() => UpdateToolbarState();
    void ITabActivationHost.ShowWorkspaceTab(BrowserTab tab) => ShowWorkspaceTab(tab);
    void ITabActivationHost.ShowFolderTab(BrowserTab tab) => ShowFolderTab(tab);
    void ITabActivationHost.QueueSessionSave() => QueueSessionSave();
    void ITabActivationHost.RefreshSplitViewTabVisualStates() => RefreshSplitViewTabVisualStates();
    bool ITabActivationHost.ShowActiveSplitView() => ShowActiveSplitView();
    bool ITabActivationHost.IsNewTabUrl(string? url) => IsNewTabUrl(url);
    bool ITabActivationHost.IsBlankUrl(string? url) => IsBlankUrl(url);
    void ITabActivationHost.SetLastActiveBrowserTabId(Guid tabId) => _lastActiveBrowserTabId = tabId;
    void ITabActivationHost.QueueBackgroundTabLoading() => QueueBackgroundTabLoading();
    void ITabActivationHost.QueueBackgroundThumbnailWarmup() => QueueBackgroundThumbnailWarmup();
    void ITabActivationHost.ShowWebViewRecoveryPlaceholder(BrowserTab tab, string title, string detail)
        => ShowWebViewRecoveryPlaceholder(tab, title, detail);
    Task<bool> ITabActivationHost.EnsureSplitViewTabsLoadedAsync(CancellationToken cancellationToken)
        => EnsureSplitViewTabsLoadedAsync(cancellationToken);
    void ITabActivationHost.QueueTabAudioPanApply(BrowserTab tab) => QueueTabAudioPanApply(tab);
    bool ITabActivationHost.HasLoadedRuntime(BrowserTab tab) => _webViewLifecycleService.HasLoadedRuntime(tab);
    void ITabActivationHost.ShowWebView(BrowserTab tab) => ShowWebView(tab);
    void ITabActivationHost.ShowMemoryRestorePlaceholder(BrowserTab tab) => ShowMemoryRestorePlaceholder(tab);
    bool ITabActivationHost.CanUseTabRuntimeInCurrentWindow(BrowserTab tab) => CanUseTabRuntimeInCurrentWindow(tab);
    void ITabActivationHost.PlaceWebViewForCurrentActivation(BrowserTab tab) => PlaceWebViewForCurrentActivation(tab);
    Task ITabActivationHost.InitializeTabWebViewRuntimeAsync(BrowserTab tab, string? initialUrl, WebViewInitializationLease lease)
        => InitializeTabWebViewRuntimeAsync(tab, initialUrl, lease);
    void ITabActivationHost.NavigateCore(BrowserTab tab, string url) => NavigateCore(tab, url);
    void ITabActivationHost.SetStatus(string message) => SetStatus(message);

    private async Task InitializeTabWebViewRuntimeAsync(
        BrowserTab tab,
        string? initialUrl,
        WebViewInitializationLease initializationLease)
    {
        var totalStart = Stopwatch.GetTimestamp();
        var initializationStage = "create-control";
        CoreWebView2Environment? environment = null;
        try
        {
            var webView = GetTabWebView(tab) ?? CreateWebView(tab);

            PlaceWebViewForCurrentActivation(tab);

            var ensureCoreStart = Stopwatch.GetTimestamp();
            initializationStage = "environment";
            environment = await EnsureWebViewEnvironmentAsync(tab.ContainerProfileId);
            if (!initializationLease.IsCurrent)
            {
                return;
            }

            MarkStartupPerformance(StartupPerformanceStage.EnvironmentReady, tab);
            initializationStage = "ensure-core";
            await webView.EnsureCoreWebView2Async(environment);
            if (!initializationLease.IsCurrent)
            {
                return;
            }

            MarkStartupPerformance(StartupPerformanceStage.CoreWebViewReady, tab);
            AppendWebViewPerformanceDiagnostic(
                "webview-ensure-core",
                tab,
                initialUrl,
                GetElapsedMilliseconds(ensureCoreStart));

            if (ActiveTab?.Id != tab.Id && !IsVisibleOrPreparingSplitPaneNavigation(tab))
            {
                YouTubePlaybackStateService.MarkBackgroundNavigationPaused(tab, initialUrl);
            }

            initializationStage = "configure";
            await ConfigureWebViewAsync(tab, initializationLease: initializationLease);
            if (!initializationLease.IsCurrent)
            {
                return;
            }

            MarkStartupPerformance(StartupPerformanceStage.WebViewConfigured, tab);
            _ = s_tabRuntimeCoordinator.SetLoaded(tab, true);
            ClearWebViewLoadFailure(tab);
            QueueBackgroundThumbnailWarmup();
            AppendWebViewPerformanceDiagnostic(
                "webview-runtime-ready",
                tab,
                initialUrl,
                GetElapsedMilliseconds(totalStart));
        }
        catch (Exception ex)
        {
            if (initializationLease.IsCurrent)
            {
                AppendWebViewInitializationFailureResourceDiagnostic(
                    tab,
                    initialUrl,
                    ex,
                    initializationStage,
                    environment);
                AppendWebViewPerformanceDiagnostic(
                    "webview-runtime-failed",
                    tab,
                    initialUrl,
                    GetElapsedMilliseconds(totalStart),
                    $"type={ex.GetType().Name};message={ex.Message}");
            }

            throw;
        }
    }

    private void MarkWebViewLoadFailure(BrowserTab tab, string message)
    {
        _webViewLifecycleService.MarkLoadFailure(tab, message);
    }

    private void ClearWebViewLoadFailure(BrowserTab tab)
    {
        _webViewLifecycleService.ClearLoadFailure(tab);
    }

    private void ResetWebViewLoadFailureForRetry(BrowserTab tab)
    {
        if (_webViewLifecycleService.ShouldDisposeRuntimeForRetry(tab))
        {
            ReleaseTabRuntimeResources(tab);
            return;
        }

        ClearWebViewLoadFailure(tab);
    }

    private void ShowWebViewRecoveryPlaceholder(BrowserTab tab, string title, string detail)
    {
        if (_splitViewService.Contains(tab))
        {
            ClearSplitViewForSingleTab(null);
        }

        if (GetTabWebView(tab) is not null)
        {
            MoveWebViewToBackground(tab);
        }

        ShowPlaceholder(CreateWebViewRecoveryView(tab, title, detail));
        _displayedTab = null;
    }

    private void PlaceWebViewForCurrentActivation(BrowserTab tab)
    {
        if (!CanUseTabRuntimeInCurrentWindow(tab))
        {
            return;
        }

        if (_splitViewWindowStateCoordinator.IsPreparing && _splitViewService.Contains(tab))
        {
            PlaceWebViewForSplitPreparation(tab);
            return;
        }

        if (ActiveTab == tab)
        {
            ShowWebView(tab);
        }
        else
        {
            MoveWebViewToBackground(tab);
        }
    }

    private bool IsVisibleOrPreparingSplitPaneNavigation(BrowserTab tab)
    {
        return _splitViewService.Contains(tab)
            && (_splitViewWindowStateCoordinator.IsPreparing || SplitViewHost.Visibility == Visibility.Visible);
    }

    private WebView2 CreateWebView(BrowserTab tab)
    {
        var webMessageToken = WebMessageValidationService.CreateToken();
        var webView = new WebView2();
        var registeredWithRuntime = false;
        try
        {
            s_tabRuntimeCoordinator.RegisterWebView(tab, webView, webMessageToken);
            registeredWithRuntime = true;
            RebindWebViewControlEventHandlers(tab, webView);
            return webView;
        }
        catch (Exception operationException)
        {
            try
            {
                if (registeredWithRuntime)
                {
                    s_tabRuntimeCoordinator.ReleaseResources(tab, static _ => { });
                }
                else
                {
                    webView.Dispose();
                }
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException(
                    "WebView2の作成失敗後に資源を解放できませんでした。",
                    operationException,
                    cleanupException);
            }

            throw;
        }
    }

    private WebViewEventSubscription AttachWebViewEventHandlersCore(
        BrowserTab tab,
        WebView2 webView)
    {
        RoutedEventHandler gotFocus = (_, _) => ActivateSplitPaneForTab(tab);
        KeyboardFocusChangedEventHandler gotKeyboardFocus =
            (_, _) => ActivateSplitPaneForTab(tab);
        EventHandler<EventArgs> zoomFactorChanged =
            (_, _) => HandlePageZoomFactorChanged(tab, webView);

        EventHandler<CoreWebView2NavigationStartingEventArgs> navigationStarting = (_, args) =>
        {
            if (!_tabs.Contains(tab))
            {
                return;
            }

            if (args.IsUserInitiated
                && !ShouldPreservePendingSavedNavigationHistory(tab, args.Uri, args.IsRedirected))
            {
                tab.PendingSavedNavigationHistoryIndex = -1;
            }

            CancelInputInteractionForTab(tab);
            MarkAmazonPrimeVideoNavigationStarting(tab, args.Uri);
            ClosePageFindForTab(tab);
            BeginPageZoomNavigation(tab);
            ResetYouTubePlaybackDiagnostic("tab", tab.Id);
            CancelInactiveYouTubeTabCheck(tab);
            tab.IsYouTubeLoadStoppedForInactivity = false;
            var normalizedUri = NormalizeYouTubeShortsUrl(args.Uri);
            if (!normalizedUri.Equals(args.Uri, StringComparison.OrdinalIgnoreCase))
            {
                args.Cancel = true;
                tab.Url = normalizedUri;
                if (ActiveTab == tab)
                {
                    SetAddressText(normalizedUri);
                }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_tabs.Contains(tab))
                    {
                        NavigateCore(tab, normalizedUri);
                    }
                }), DispatcherPriority.Normal);
                return;
            }

            if ((ReferenceEquals(tab, ActiveTab) || IsVisibleOrPreparingSplitPaneNavigation(tab))
                && IsYouTubeVideoUrl(normalizedUri))
            {
                DeferBackgroundWork(YouTubeNavigationBackgroundWorkDeferralMilliseconds);
            }

            _webViewNavigationStartTimestamps[tab.Id] = Stopwatch.GetTimestamp();
            AppendWebViewPerformanceDiagnostic(
                "webview-navigation-starting",
                tab,
                args.Uri,
                detail: $"redirected={args.IsRedirected};user={args.IsUserInitiated}");

            _ = s_tabRuntimeCoordinator.SetLoading(tab, true);
            tab.IsPlayingAudio = false;
            tab.IsAudioPanScriptInjected = false;
            _ = s_tabRuntimeCoordinator.SetFavicon(tab, null);
            tab.FaviconPngBase64 = string.Empty;
            if (IsUserGuideUrl(tab.Url) && IsUserGuideContentUrl(args.Uri))
            {
                if (ActiveTab == tab)
                {
                    SetAddressText(UserGuideUrl);
                }
            }
            else if (IsNewTabUrl(tab.Url) && IsGeneratedNewTabSource(args.Uri))
            {
                if (ActiveTab == tab)
                {
                    SetAddressText(string.Empty);
                }
            }
            else if (!IsBlankUrl(args.Uri))
            {
                tab.Url = args.Uri;
                if (ActiveTab == tab)
                {
                    SetAddressText(args.Uri);
                }
            }

            QueueInactiveYouTubeTabCheck(tab, stopStalledLoad: true);
            UpdateToolbarState();
            QueueWorkspaceOpenTabsRefresh();
        };

        EventHandler<CoreWebView2NavigationCompletedEventArgs> navigationCompleted = (_, args) =>
        {
            if (!_tabs.Contains(tab))
            {
                return;
            }

            long? elapsedMilliseconds = _webViewNavigationStartTimestamps.Remove(tab.Id, out var navigationStart)
                ? GetElapsedMilliseconds(navigationStart)
                : null;

            CancelInactiveYouTubeTabCheck(tab);
            _ = s_tabRuntimeCoordinator.SetLoading(tab, false);
            RefreshTabFromCore(tab);
            if (args.IsSuccess)
            {
                ResetYouTubeControlSuppressionAfterNavigation(tab);
            }

            if (args.IsSuccess
                && (ReferenceEquals(tab, ActiveTab) || IsVisibleOrPreparingSplitPaneNavigation(tab))
                && IsYouTubeVideoTab(tab))
            {
                DeferBackgroundWork(YouTubeNavigationCompletedBackgroundWorkDeferralMilliseconds);
            }
            if (!TryCompleteSavedNavigationHistory(tab, tab.Url, args.IsSuccess))
            {
                RecordTabNavigation(tab, tab.Url);
            }
            if (args.IsSuccess)
            {
                var isMemoryRestoreNavigation = tab.IsMemoryReleased;
                _webViewLifecycleService.MarkNavigationSuccess(tab);
                if (isMemoryRestoreNavigation)
                {
                    QueueMemoryReleasedTabRestore(tab);
                }

                if (!isMemoryRestoreNavigation)
                {
                    App.BrowsingHistory.EnqueueVisit(new BrowsingHistoryVisit(
                        tab.Id,
                        GetCurrentPageUrl(tab) ?? tab.Url ?? string.Empty,
                        tab.DisplayTitle,
                        tab.ContainerProfileId,
                        BrowsingHistorySourceKind.WebTab,
                        DateTimeOffset.UtcNow),
                        isPrivate: _isPrivateWindow);
                    var recentUrl = GetCurrentPageUrl(tab) ?? tab.Url ?? string.Empty;
                    if (!IsBlankUrl(recentUrl))
                    {
                        RecordRecentItem(RecentItemFactory.FromWeb(recentUrl, tab.DisplayTitle, containerProfileId: tab.ContainerProfileId));
                    }
                }
            }

            QueueTabFaviconRefresh(tab);
            UpdateToolbarState();
            QueueYouTubePlaybackRestoreAttempt(tab);
            QueueYouTubePlaybackStateBridgeAttempt(tab);
            UpdateYouTubePlaybackStateTracking(ActiveTab);
            if (args.IsSuccess)
            {
                QueueAmazonPrimeVideoReadinessCheck(tab);
                QueueRestoredTabBridgeCompatibilityProbe(tab);
            }
            else
            {
                MarkAmazonPrimeVideoNavigationFailed(tab);
            }
            CompleteStreamingPlaybackCompatibilityRetry(tab, args.IsSuccess);
            QueueXReadableModeAttempt(tab);
            QueueChatGptReadabilityAttempt(tab);
            QueueFiveChLiveReadabilityAttempt(tab);
            QueueTabAudioPanApply(tab);
            CompletePageZoomNavigation(tab, GetCurrentPageUrl(tab) ?? tab.Url);
            if (!args.IsSuccess && ActiveTab == tab)
            {
                SetStatus(
                    "ページ読み込みに失敗しました: "
                    + WebViewLifecycleService.FormatNavigationError(args.WebErrorStatus),
                    autoClear: true);
            }

            AppendWebViewPerformanceDiagnostic(
                "webview-navigation-completed",
                tab,
                tab.Url,
                elapsedMilliseconds,
                $"success={args.IsSuccess};error={args.WebErrorStatus};http={args.HttpStatusCode}");
            MarkSelectedTabNavigationCompleted(tab);

            QueueBackgroundTabLoading();
            QueueBackgroundThumbnailWarmup();
            QueueSessionSave();
            QueueWorkspaceOpenTabsRefresh();
        };

        var subscription = new WebViewEventSubscription(
            webView,
            gotFocus,
            gotKeyboardFocus,
            zoomFactorChanged,
            navigationStarting,
            navigationCompleted);
        subscription.Attach();
        return subscription;
    }

    private void DetachWebViewEventHandlers(
        BrowserTab tab,
        WebViewEventSubscription subscription)
    {
        List<Exception>? exceptions = null;
        void RunCleanup(Action cleanup)
        {
            try
            {
                cleanup();
            }
            catch (Exception ex)
            {
                (exceptions ??= []).Add(ex);
            }
        }

        RunCleanup(() => ResetNativeWebGestureForTab(tab));
        RunCleanup(subscription.Dispose);
        if (exceptions is { Count: 1 })
        {
            throw exceptions[0];
        }

        if (exceptions is { Count: > 1 })
        {
            throw new AggregateException("WebView2コントロールのイベント購読を解除できませんでした。", exceptions);
        }
    }

    private Task<CoreWebView2Environment> EnsureWebViewEnvironmentAsync(string? containerProfileId = null)
    {
        return _webViewEnvironmentService.GetEnvironmentAsync(
            containerProfileId,
            GetBrowserExtensionsEnabledForEnvironment());
    }

    private bool GetBrowserExtensionsEnabledForEnvironment()
    {
        return s_webViewEnvironmentExtensionModeCoordinator.Resolve(
            _isPrivateWindow,
            _settings.EnableBrowserExtensions);
    }

    private async Task ConfigureWebViewAsync(
        BrowserTab tab,
        bool addGestureScript = true,
        WebViewInitializationLease? initializationLease = null)
    {
        bool IsInitializationCurrent()
        {
            return initializationLease is null || initializationLease.Value.IsCurrent;
        }

        if (!IsInitializationCurrent())
        {
            return;
        }

        if (GetTabWebView(tab)?.CoreWebView2 is not { } core)
        {
            return;
        }

        var start = Stopwatch.GetTimestamp();
        try
        {
            WebViewLifecycleService.ApplyDefaultCoreSettings(core);

            await CoreWebViewDocumentScriptRegistry.EnsureFixedAsync(
                core,
                "amazon-bridge-suppression",
                AmazonPrimeVideoService.BuildWebViewBridgeSuppressionScript(GetTabWebMessageToken(tab)));
            if (!IsInitializationCurrent())
            {
                return;
            }

            await CoreWebViewDocumentScriptRegistry.EnsureFixedAsync(
                core,
                "tab-audio-pan-bootstrap",
                BuildTabAudioPanBootstrapScript());
            if (!IsInitializationCurrent())
            {
                return;
            }

            await CoreWebViewDocumentScriptRegistry.EnsureFixedAsync(
                core,
                "tab-memory-protection",
                BuildTabMemoryProtectionScript());
            if (!IsInitializationCurrent())
            {
                return;
            }

            await CoreWebViewDocumentScriptRegistry.EnsureFixedAsync(
                core,
                "streaming-playback-failure-bridge",
                StreamingPlaybackCompatibilityService.BuildFailureBridgeScript(
                    GetTabWebMessageToken(tab) ?? string.Empty));
            if (!IsInitializationCurrent())
            {
                return;
            }

            await CoreWebViewDocumentScriptRegistry.EnsureFixedAsync(
                core,
                "youtube-playback-diagnostic-bridge",
                YouTubePlaybackDiagnosticService.BuildBridgeScript(GetTabWebMessageToken(tab) ?? string.Empty));
            if (!IsInitializationCurrent())
            {
                return;
            }

            await CoreWebViewDocumentScriptRegistry.EnsureFixedAsync(
                core,
                "youtube-control-activity-bridge",
                YouTubeControlScriptService.BuildPlayerRectScript(GetTabWebMessageToken(tab)));
            if (!IsInitializationCurrent())
            {
                return;
            }

            if (tab.YouTubePlaybackIsPaused == true
                && !string.IsNullOrWhiteSpace(tab.YouTubePlaybackVideoId))
            {
                await CoreWebViewDocumentScriptRegistry.UpdateVariableAsync(
                    core,
                    "youtube-paused-resume-guard",
                    YouTubeControlScriptService.BuildPausedResumeGuardScript(tab.YouTubePlaybackVideoId));
                if (!IsInitializationCurrent())
                {
                    return;
                }
            }
            else
            {
                CoreWebViewDocumentScriptRegistry.RemoveVariable(
                    core,
                    "youtube-paused-resume-guard");
            }

            AttachCoreWebViewEventHandlers(tab, core);
            ApplyTabMuteState(tab);
            QueueTabAudioPanApply(tab);
            RefreshTabAudioState(tab);
            ApplySavedPageZoom(tab);
            if (addGestureScript)
            {
                await CoreWebViewDocumentScriptRegistry.UpdateVariableAsync(
                    core,
                    "gesture-host",
                    BuildGestureScript(tab));
                if (!IsInitializationCurrent())
                {
                    return;
                }
            }

            await RefreshExtensionToolbarAsync(core.Profile);
        }
        finally
        {
            AppendWebViewPerformanceDiagnostic(
                "webview-configure",
                tab,
                GetCurrentPageUrl(tab) ?? tab.Url,
                GetElapsedMilliseconds(start),
                $"gesture={addGestureScript}");
        }
    }

    private void AttachCoreWebViewEventHandlers(BrowserTab tab, CoreWebView2 core)
    {
        if (GetTabWebView(tab) is { } webView)
        {
            RebindWebViewRuntimeEventHandlers(tab, webView, core);
        }
    }

    private CoreWebViewEventSubscription AttachCoreWebViewEventHandlersCore(
        BrowserTab tab,
        CoreWebView2 core)
    {

        var contextMenuRequested = CreateWebContextMenuRequestedHandler(tab, core);

        EventHandler<CoreWebView2FrameCreatedEventArgs> frameCreated =
            (_, args) => RegisterTabAudioPanFrame(tab, args.Frame);

        EventHandler<object> documentTitleChanged = (_, _) =>
        {
            if (!_tabs.Contains(tab))
            {
                return;
            }

            tab.Title = core.DocumentTitle;
            QueueSessionSave();
            QueueWorkspaceOpenTabsRefresh();
        };

        EventHandler<CoreWebView2SourceChangedEventArgs> sourceChanged = (_, _) =>
        {
            if (!_tabs.Contains(tab))
            {
                return;
            }

            HideLinkPreview(stopPreviewPlayback: true);
            ResetYouTubePlaybackDiagnostic("tab", tab.Id);
            var normalizedSource = NormalizeYouTubeShortsUrl(core.Source);
            if (!normalizedSource.Equals(core.Source, StringComparison.OrdinalIgnoreCase))
            {
                tab.Url = normalizedSource;
                if (ActiveTab == tab)
                {
                    SetAddressText(normalizedSource);
                }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_tabs.Contains(tab))
                    {
                        NavigateCore(tab, normalizedSource, replaceCurrentHistoryEntry: true);
                    }
                }), DispatcherPriority.Normal);
                return;
            }

            RefreshTabFromCore(tab);
            if ((ReferenceEquals(tab, ActiveTab) || IsVisibleOrPreparingSplitPaneNavigation(tab))
                && IsYouTubeVideoTab(tab))
            {
                DeferBackgroundWork(YouTubeNavigationBackgroundWorkDeferralMilliseconds);
            }
            RecordTabNavigation(tab, tab.Url);
            ApplySavedPageZoom(tab, core.Source);
            UpdateToolbarState();
            QueueYouTubePlaybackRestoreAttempt(tab);
            QueueYouTubePlaybackStateBridgeAttempt(tab);
            UpdateYouTubePlaybackStateTracking(ActiveTab);
            QueueAmazonPrimeVideoReadinessCheck(tab);
            QueueXReadableModeAttempt(tab);
            QueueChatGptReadabilityAttempt(tab);
            QueueFiveChLiveReadabilityAttempt(tab);
            QueueWorkspaceOpenTabsRefresh();
        };

        EventHandler<object> historyChanged = (_, _) =>
        {
            if (_tabs.Contains(tab))
            {
                UpdateToolbarState();
            }
        };
        EventHandler<object> faviconChanged = (_, _) =>
        {
            if (_tabs.Contains(tab))
            {
                QueueTabFaviconRefresh(tab);
            }
        };
        EventHandler<object> isDocumentPlayingAudioChanged = (_, _) =>
        {
            if (_tabs.Contains(tab))
            {
                RefreshTabAudioState(tab);
                QueueNativeTabAudioPanRetries(tab);
            }
        };
        EventHandler<object> isMutedChanged = (_, _) =>
        {
            if (_tabs.Contains(tab))
            {
                RefreshTabMuteState(tab);
            }
        };
        EventHandler<object> containsFullScreenElementChanged = (_, _) =>
        {
            if (_tabs.Contains(tab))
            {
                ApplyYouTubeApplicationFullscreen(core.ContainsFullScreenElement);
            }
        };
        EventHandler<object> windowCloseRequested = (_, _) =>
        {
            if (!_newWindowPopupTabIds.Contains(tab.Id))
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_tabs.Contains(tab))
                {
                    CloseTab(tab);
                }
            }), DispatcherPriority.Normal);
        };

        EventHandler<CoreWebView2NewWindowRequestedEventArgs> newWindowRequested = (_, args) =>
        {
            if (!_tabs.Contains(tab))
            {
                return;
            }

            if (ShouldOpenNewWindowRequestInForegroundTab(tab, args.Uri))
            {
                _ = OpenNewWindowRequestInForegroundTabWithDeferralAsync(tab, args);
                return;
            }

            if (!args.IsUserInitiated)
            {
                args.Handled = true;
                HandleAutomaticPopup(tab, args.Uri);
                return;
            }

            args.Handled = true;
            if (!string.IsNullOrWhiteSpace(args.Uri))
            {
                OpenBackgroundTabNextTo(tab, args.Uri);
                SetStatus("リンクを右隣の裏タブで開きました", autoClear: true);
            }
        };
        EventHandler<CoreWebView2PermissionRequestedEventArgs> permissionRequested = (_, args) =>
        {
            if (!_tabs.Contains(tab) || !SitePrivacyService.IsManagedPermission(args.PermissionKind))
            {
                return;
            }

            args.Handled = true;
            var deferral = args.GetDeferral();
            _ = HandleSitePermissionRequestedAsync(tab, args, deferral);
        };
        EventHandler<CoreWebView2ScreenCaptureStartingEventArgs> screenCaptureStarting = (_, args) =>
        {
            if (!_tabs.Contains(tab))
            {
                args.Cancel = true;
                return;
            }

            args.Handled = true;
            var deferral = args.GetDeferral();
            _ = HandleScreenCaptureStartingAsync(tab, args, deferral);
        };
        EventHandler<CoreWebView2DownloadStartingEventArgs> downloadStarting = (_, args) =>
        {
            if (_tabs.Contains(tab))
            {
                HandleDownloadStarting(tab, args);
            }
        };

        EventHandler<CoreWebView2WebMessageReceivedEventArgs> webMessageReceived = (_, args) =>
        {
            if (_tabs.Contains(tab))
            {
                HandleWebMessage(tab, args);
            }
        };
        EventHandler<CoreWebView2ProcessFailedEventArgs> processFailed = (_, args) =>
        {
            if (!_tabs.Contains(tab))
            {
                return;
            }

            var failure = WebViewLifecycleService.FormatProcessFailure(args);
            var action = WebViewProcessFailurePolicy.GetAction(args.ProcessFailedKind);
            HandleYouTubePlaybackProcessFailure(
                "tab",
                tab.Id,
                GetCurrentPageUrl(tab) ?? tab.Url,
                args);
            ClosePageFindForTab(tab);
            CancelPageZoomNavigation(tab);
            s_webViewProcessRecoveryCoordinator.RecordProcessFailure(
                tab.ContainerProfileId,
                TryGetBrowserProcessId(core),
                args.ProcessFailedKind,
                action);

            if (action == WebViewProcessFailureAction.ObserveOnly)
            {
                if (ActiveTab == tab)
                {
                    SetStatus("WebView2 が補助プロセスを自動復旧しています: " + failure, autoClear: true);
                }

                return;
            }

            if (action == WebViewProcessFailureAction.RevalidateMediaPlayback)
            {
                var failedUrl = GetCurrentPageUrl(tab) ?? tab.Url;
                if (!string.IsNullOrWhiteSpace(failedUrl)
                    && WebViewProcessFailurePolicy.ShouldRevalidateMediaPlayback(
                        args.ProcessFailedKind,
                        failedUrl))
                {
                    QueueWebViewGpuMediaRecovery(tab, core, failedUrl, failure, args.ProcessFailedKind);
                }
                else if (ActiveTab == tab)
                {
                    SetStatus("WebView2 がGPUプロセスを自動復旧しています", autoClear: true);
                }

                return;
            }

            _ = s_tabRuntimeCoordinator.SetLoading(tab, false);
            UpdateToolbarState();
            if (action == WebViewProcessFailureAction.ReloadAffectedRuntime)
            {
                QueueWebViewRenderProcessReload(tab, core, failure, args.ProcessFailedKind);
                return;
            }

            MarkWebViewLoadFailure(tab, failure);
            if (ActiveTab == tab)
            {
                SetStatus("WebView2 ブラウザープロセスの終了を確認しています", autoClear: true);
                ShowWebViewRecoveryPlaceholder(tab, "WebView2 プロセス異常", failure);
            }
        };

        var subscription = new CoreWebViewEventSubscription(
            core,
            frameCreated,
            documentTitleChanged,
            sourceChanged,
            historyChanged,
            faviconChanged,
            isDocumentPlayingAudioChanged,
            isMutedChanged,
            containsFullScreenElementChanged,
            contextMenuRequested,
            windowCloseRequested,
            newWindowRequested,
            permissionRequested,
            screenCaptureStarting,
            downloadStarting,
            webMessageReceived,
            processFailed);
        subscription.Attach();
        return subscription;
    }

    private void DetachCoreWebViewEventHandlers(
        BrowserTab tab,
        CoreWebViewEventSubscription subscription)
    {
        subscription.Dispose();
    }

    private void HandleDownloadStarting(BrowserTab tab, CoreWebView2DownloadStartingEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            HandleDownloadStartingCore(tab, args);
        }
        catch (Exception ex)
        {
            args.Cancel = true;
            args.Handled = true;
            SetStatus("ダウンロードを開始できません: " + ex.Message, autoClear: true);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void HandleDownloadStartingCore(BrowserTab tab, CoreWebView2DownloadStartingEventArgs args)
    {
        var operation = args.DownloadOperation;
        var suggestedFileName = _downloadPathResolver.GetSuggestedFileName(args.ResultFilePath, operation.Uri);
        string selectedPath;
        if (_settings.Downloads.AskSaveLocation)
        {
            var dialog = new SaveFileDialog
            {
                Title = "ダウンロード先を選択",
                InitialDirectory = ResolveDownloadInitialDirectory(args.ResultFilePath),
                FileName = suggestedFileName,
                Filter = "すべてのファイル (*.*)|*.*",
                OverwritePrompt = false,
                CheckPathExists = false,
                AddExtension = true
            };

            if (dialog.ShowDialog(this) != true)
            {
                args.Cancel = true;
                args.Handled = true;
                SetStatus("ダウンロードをキャンセルしました", autoClear: true);
                return;
            }

            selectedPath = _downloadPathResolver.ResolveUniquePath(dialog.FileName);
        }
        else
        {
            selectedPath = _downloadPathResolver.ResolveUniquePath(
                Path.Combine(ResolveDownloadInitialDirectory(args.ResultFilePath), suggestedFileName));
        }

        var safety = _downloadPolicy.InspectFileName(Path.GetFileName(selectedPath));
        if (safety.ShouldWarn && !ConfirmDownloadSafety(safety, Path.GetFileName(selectedPath), operation.Uri))
        {
            args.Cancel = true;
            args.Handled = true;
            SetStatus("危険ファイル警告でダウンロードをキャンセルしました", autoClear: true);
            return;
        }

        args.ResultFilePath = selectedPath;
        args.Handled = true;
        var item = _downloadService.TrackDownload(
            operation,
            selectedPath,
            safety,
            tab.ContainerProfileId);
        _downloadShelfController?.Refresh();
        UpdateDownloadsButtonState();
        OpenDownloadShelfForStart();
        SetStatus($"ダウンロードを開始しました: {item.FileName}", autoClear: true);
    }

    private string ResolveDownloadInitialDirectory(string? resultFilePath)
    {
        var configured = _settings.Downloads.DefaultSaveDirectory;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return _downloadPathResolver.ResolveInitialDirectory(resultFilePath);
    }

    private bool ConfirmDownloadSafety(DownloadSafetyResult safety, string fileName, string sourceUrl)
    {
        var message = string.Join(
            Environment.NewLine,
            safety.WarningText,
            string.Empty,
            "保存する場合は、入手元とファイル名を確認してください。",
            string.Empty,
            "ファイル: " + fileName,
            "URL: " + TrimDialogText(sourceUrl, 260),
            string.Empty,
            "保存を続行しますか？");
        return MessageBox.Show(
            this,
            message,
            "危険な可能性のあるダウンロード",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private bool ConfirmOpenDangerousDownload(DownloadItem item)
    {
        var message = string.Join(
            Environment.NewLine,
            string.IsNullOrWhiteSpace(item.SafetyWarning)
                ? "危険な可能性のあるファイルです。"
                : item.SafetyWarning,
            string.Empty,
            "開く前に、入手元とファイル名を確認してください。",
            string.Empty,
            "ファイル: " + item.FilePath,
            "URL: " + TrimDialogText(item.SourceUrl, 260),
            string.Empty,
            "このファイルを開きますか？");
        return MessageBox.Show(
            this,
            message,
            "危険な可能性のあるファイルを開く",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private static string TrimDialogText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length <= maxLength)
        {
            return value ?? string.Empty;
        }

        return value[..Math.Max(0, maxLength - 1)] + "…";
    }

    private void RebindWebViewRuntimeEventHandlers(
        BrowserTab tab,
        WebView2 webView,
        CoreWebView2 core)
    {
        if (s_tabRuntimeCoordinator.TryAugmentEventOwnership(
                tab,
                this,
                webView,
                expectedGeneration: 0,
                newGeneration: 1,
                () => AttachCoreWebViewRuntimeEventHandlersCore(tab, core)))
        {
            return;
        }

        s_tabRuntimeCoordinator.RebindEventOwnership(
            tab,
            this,
            webView,
            sourceGeneration: 1,
            () => AttachWebViewRuntimeEventHandlersCore(tab, webView, core));
    }

    private void RebindWebViewControlEventHandlers(BrowserTab tab, WebView2 webView)
    {
        s_tabRuntimeCoordinator.RebindEventOwnership(
            tab,
            this,
            webView,
            sourceGeneration: 0,
            () => RegisterWebViewControlEventSubscription(tab, webView));
    }

    private IDisposable RegisterWebViewControlEventSubscription(
        BrowserTab tab,
        WebView2 webView)
    {
        var subscription = new WebViewControlEventSubscription(
            this,
            tab,
            AttachWebViewEventHandlersCore(tab, webView));
        try
        {
            return s_tabRuntimeCoordinator.RegisterEventSubscription(
                tab,
                TabRuntimeEventSubscriptionKind.WebViewControl,
                _workspaceWindowId,
                webView,
                subscription);
        }
        catch (Exception registrationException)
        {
            try
            {
                subscription.Dispose();
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException(
                    "WebView2コントロールのイベント購読登録と失敗後の解除に失敗しました。",
                    registrationException,
                    cleanupException);
            }

            throw;
        }
    }

    private IDisposable AttachCoreWebViewRuntimeEventHandlersCore(
        BrowserTab tab,
        CoreWebView2 core)
    {
        var subscription = new CoreWebViewRuntimeEventSubscription(
            this,
            tab,
            AttachCoreWebViewEventHandlersCore(tab, core));
        try
        {
            return s_tabRuntimeCoordinator.RegisterEventSubscription(
                tab,
                TabRuntimeEventSubscriptionKind.CoreWebView,
                _workspaceWindowId,
                core,
                subscription);
        }
        catch (Exception registrationException)
        {
            try
            {
                subscription.Dispose();
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException(
                    "CoreWebView2のイベント購読登録と失敗後の解除に失敗しました。",
                    registrationException,
                    cleanupException);
            }

            throw;
        }
    }

    private IDisposable AttachWebViewRuntimeEventHandlersCore(
        BrowserTab tab,
        WebView2 webView,
        CoreWebView2 core)
    {
        IDisposable? webViewSubscription = null;
        IDisposable? coreSubscription = null;
        try
        {
            webViewSubscription = RegisterWebViewControlEventSubscription(tab, webView);
            coreSubscription = AttachCoreWebViewRuntimeEventHandlersCore(tab, core);

            return new WebViewRuntimeEventSubscription(
                webViewSubscription,
                coreSubscription);
        }
        catch (Exception attachException)
        {
            List<Exception>? cleanupExceptions = null;
            try
            {
                coreSubscription?.Dispose();
            }
            catch (Exception ex)
            {
                (cleanupExceptions ??= []).Add(ex);
            }

            try
            {
                webViewSubscription?.Dispose();
            }
            catch (Exception ex)
            {
                (cleanupExceptions ??= []).Add(ex);
            }

            if (cleanupExceptions is not null)
            {
                throw new AggregateException(
                    "WebView2イベント購読の登録と失敗後の解除に失敗しました。",
                    [attachException, .. cleanupExceptions]);
            }

            throw;
        }
    }

    private void DetachWebViewRuntimeEventHandlers(BrowserTab tab)
    {
        _ = s_tabRuntimeCoordinator.DetachEventOwnership(tab, this);
    }

    private void ToggleTabMute(BrowserTab tab)
    {
        if (tab.IsNativeContentTab)
        {
            return;
        }

        var isMuted = !tab.IsMuted;
        try
        {
            if (GetTabWebView(tab)?.CoreWebView2 is { } core)
            {
                core.IsMuted = isMuted;
            }

            tab.IsMuted = isMuted;
            RefreshTabAudioState(tab);
            QueueSessionSave();
            SetStatus(isMuted ? "このタブをミュートしました" : "このタブのミュートを解除しました", autoClear: true);
        }
        catch (Exception ex)
        {
            SetStatus("タブのミュートを変更できません: " + ex.Message, autoClear: true);
        }
    }

    private async Task<bool> SetTabAudioBoostAsync(BrowserTab tab, int audioBoostDecibels)
    {
        if (!IsTabAudioBoostEligible(tab) || GetTabWebView(tab)?.CoreWebView2 is not { } core)
        {
            return false;
        }

        return await _tabAudioController.SetBoostAsync(
            tab, audioBoostDecibels, script => WebViewUserGestureScriptService.ExecuteAsync(core, script));
    }

    internal static bool IsSuccessfulTabAudioBoostResult(string? resultJson, int expectedAudioBoostDecibels)
    {
        return TabAudioScriptService.IsSuccessfulTabAudioBoostResult(resultJson, expectedAudioBoostDecibels);
    }

    internal static bool HasAppliedTabAudioBoostResult(string? resultJson, int expectedAudioBoostDecibels)
    {
        return TabAudioScriptService.HasAppliedTabAudioBoostResult(resultJson, expectedAudioBoostDecibels);
    }

    private async void TabAudioPanMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is not BrowserTab tab
            || (sender as MenuItem)?.Tag is not TabAudioPan audioPan)
        {
            return;
        }

        await SetTabAudioPanAsync(tab, audioPan);
    }

    private async Task SetTabAudioPanAsync(BrowserTab tab, TabAudioPan audioPan)
    {
        if (tab.IsNativeContentTab)
        {
            return;
        }

        tab.AudioPan = audioPan;
        QueueSessionSave();
        await ApplyTabAudioPanAsync(tab);
        QueueNativeTabAudioPanRetries(tab);
        SetStatus($"このタブの音声位置を {tab.AudioPanStatusText} にしました", autoClear: true);
    }

    private static void ApplyTabMuteState(BrowserTab tab)
    {
        if (tab.IsNativeContentTab || GetTabWebView(tab)?.CoreWebView2 is not { } core)
        {
            return;
        }

        core.IsMuted = tab.IsMuted;
    }

    private static void SilenceTabRuntimeAudio(BrowserTab tab)
    {
        tab.IsPlayingAudio = false;
        if (tab.IsNativeContentTab || GetTabWebView(tab)?.CoreWebView2 is not { } core)
        {
            return;
        }

        try
        {
            core.IsMuted = true;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            // Runtime終了との競合時も、タブを閉じる処理は継続する。
        }
    }

    private static void RestoreTabRuntimeAudioState(BrowserTab tab, bool isMuted)
    {
        tab.IsMuted = isMuted;
        try
        {
            ApplyTabMuteState(tab);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            // Runtime終了との競合時は、保持したモデル状態を次回の初期化で再適用する。
        }
    }

    private void QueueTabAudioPanApply(BrowserTab tab)
    {
        _windowTaskSupervisor.TryRun(
            "tab-audio-pan-apply",
            _ => ApplyTabAudioPanAsync(tab));
    }

    private async Task ApplyTabAudioPanAsync(BrowserTab tab)
    {
        if (tab.IsNativeContentTab || GetTabWebView(tab)?.CoreWebView2 is not { } core)
        {
            return;
        }

        await _tabAudioController.ApplyPanAsync(
            tab, script => core.ExecuteScriptAsync(script),
            script => WebViewUserGestureScriptService.ExecuteAsync(core, script));
    }

    private void QueueNativeTabAudioPanRetries(BrowserTab tab)
    {
        if (tab.IsNativeContentTab || GetTabWebView(tab)?.CoreWebView2 is null)
        {
            return;
        }

        var expectedPan = tab.AudioPan;
        _windowTaskSupervisor.TryRun(
            "tab-audio-pan-retries",
            cancellationToken => ApplyNativeTabAudioPanWithRetriesAsync(
                tab,
                expectedPan,
                cancellationToken));
    }

    private void QueueClosedTabNativeAudioPanReset(BrowserTab tab)
    {
        _windowTaskSupervisor.TryRun(
            "tab-audio-pan-close-reset",
            _ => ApplyNativeTabAudioPanAsync(tab, TabAudioPan.Stereo));
    }

    private async Task ApplyNativeTabAudioPanWithRetriesAsync(
        BrowserTab tab,
        TabAudioPan expectedPan,
        CancellationToken cancellationToken)
    {
        await _tabAudioController.RetryNativePanAsync(tab, expectedPan, cancellationToken);
    }

    bool ITabAudioHost.ContainsTab(BrowserTab tab) => _tabs.Contains(tab);
    bool ITabAudioHost.HasAudioDocument(BrowserTab tab) => GetTabWebView(tab)?.CoreWebView2 is not null;
    Task ITabAudioHost.ApplyToFramesAsync(BrowserTab tab, string script, ICollection<string?>? results)
        => ApplyTabAudioPanToFramesAsync(tab, script, results);
    Task ITabAudioHost.ApplyNativePanAsync(BrowserTab tab, TabAudioPan audioPan)
        => ApplyNativeTabAudioPanAsync(tab, audioPan);
    void ITabAudioHost.SetStatus(string message) => SetStatus(message, autoClear: true);

    private async Task ApplyNativeTabAudioPanAsync(BrowserTab tab, TabAudioPan audioPan)
    {
        if (tab.IsNativeContentTab || GetTabWebView(tab)?.CoreWebView2 is not { } core)
        {
            return;
        }

        await _nativeTabAudioPanController.ApplyAsync(
            new WebViewAudioPanProcessSource(core),
            () => TryGetTabHost(tab),
            audioPan,
            (stage, processIds, result) => AppendTabAudioPanDiagnostic(stage, tab, audioPan, processIds, result));
    }

    private static string? TryGetTabHost(BrowserTab tab)
    {
        return TryGetHost(GetCurrentPageUrl(tab) ?? string.Empty) ?? TryGetHost(tab.Url ?? string.Empty);
    }

    private static void AppendTabAudioPanDiagnostic(
        string stage,
        BrowserTab tab,
        TabAudioPan audioPan,
        IReadOnlyCollection<int> processIds,
        TabAudioSessionPanApplyResult result)
    {
        try
        {
            var directory = Path.Combine(AppPaths.Root, "audio-pan");
            Directory.CreateDirectory(directory);

            var logPath = Path.Combine(directory, "diagnostic.log");
            if (File.Exists(logPath) && new FileInfo(logPath).Length > TabAudioPanDiagnosticLogMaxBytes)
            {
                File.Delete(logPath);
            }

            var line = string.Join(
                " | ",
                DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture),
                $"stage={NormalizeAudioPanDiagnosticToken(stage)}",
                $"pan={audioPan}",
                $"host={NormalizeAudioPanDiagnosticToken(TryGetTabHost(tab) ?? string.Empty)}",
                $"targetIds={string.Join(",", processIds.OrderBy(static x => x))}",
                $"applied={result.AppliedSessionCount.ToString(CultureInfo.InvariantCulture)}",
                $"targetSessions={result.TargetSessionCount.ToString(CultureInfo.InvariantCulture)}",
                $"activeTargetSessions={result.ActiveTargetSessionCount.ToString(CultureInfo.InvariantCulture)}",
                $"sessions={NormalizeAudioPanDiagnosticToken(string.Join(" ; ", result.DiagnosticLines))}");
            File.AppendAllText(logPath, line + Environment.NewLine);
        }
        catch
        {
            // 診断ログの失敗で通常ブラウジングや音声制御は止めない。
        }
    }

    private static string NormalizeAudioPanDiagnosticToken(string value)
    {
        var normalized = string.Join(
            " ",
            (value ?? string.Empty).Split(
                ['\r', '\n', '\t', '|'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        const int maxLength = 12000;
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength];
    }

    private void RegisterTabAudioPanFrame(BrowserTab tab, CoreWebView2Frame frame)
    {
        if (tab.IsNativeContentTab)
        {
            return;
        }

        if (!s_tabAudioPanFrameSubscriptionOwner.TryRegister(tab, frame))
        {
            return;
        }

        QueueTabAudioPanFrameInitialize(tab, frame);
    }

    private static void QueueTabAudioPanFrameInitializeForCurrentOwner(
        BrowserTab tab,
        CoreWebView2Frame frame)
    {
        if (TryGetTabRuntimeOwner(tab, out var owner) && owner is not null)
        {
            owner.QueueTabAudioPanFrameInitialize(tab, frame);
        }
    }

    private static void QueueTabAudioPanFrameApplyForCurrentOwner(
        BrowserTab tab,
        CoreWebView2Frame frame)
    {
        if (TryGetTabRuntimeOwner(tab, out var owner) && owner is not null)
        {
            owner.QueueTabAudioPanFrameApply(tab, frame);
        }
    }

    private static void RegisterTabAudioPanFrameForCurrentOwner(
        BrowserTab tab,
        CoreWebView2Frame frame)
    {
        if (TryGetTabRuntimeOwner(tab, out var owner) && owner is not null)
        {
            owner.RegisterTabAudioPanFrame(tab, frame);
        }
    }

    private void QueueTabAudioPanFrameInitialize(BrowserTab tab, CoreWebView2Frame frame)
    {
        _windowTaskSupervisor.TryRun(
            "tab-audio-pan-frame-initialize",
            _ => InitializeTabAudioPanFrameAsync(tab, frame));
    }

    private void QueueTabAudioPanFrameApply(BrowserTab tab, CoreWebView2Frame frame)
    {
        _windowTaskSupervisor.TryRun(
            "tab-audio-pan-frame-apply",
            _ => ApplyTabAudioPanToFrameAsync(tab, frame));
    }

    private static bool TryGetTabRuntimeOwner(BrowserTab tab, out MainWindow? owner)
    {
        return s_tabRuntimeCoordinator.TryGetEventOwner(tab, out owner);
    }

    private async Task ApplyTabAudioPanToFramesAsync(
        BrowserTab tab,
        string script,
        ICollection<string?>? results = null)
    {
        foreach (var frame in s_tabAudioPanFrameSubscriptionOwner.CreateFrameSnapshot(tab))
        {
            var result = await ApplyTabAudioPanToFrameAsync(tab, frame, script);
            results?.Add(result);
        }
    }

    private async Task InitializeTabAudioPanFrameAsync(BrowserTab tab, CoreWebView2Frame frame)
    {
        if (!_tabs.Contains(tab) || tab.IsNativeContentTab)
        {
            return;
        }

        try
        {
            await frame.ExecuteScriptAsync(BuildTabAudioPanBootstrapScript());
        }
        catch
        {
            // frame の document がまだ実行可能でない場合は ContentLoading / DOMContentLoaded で再試行する。
        }

        await ApplyTabAudioPanToFrameAsync(tab, frame);
    }

    private async Task<string?> ApplyTabAudioPanToFrameAsync(
        BrowserTab tab,
        CoreWebView2Frame frame,
        string? script = null)
    {
        if (!_tabs.Contains(tab)
            || (script is null
                && tab.AudioPan == TabAudioPan.Stereo
                && tab.AudioBoostDecibels == 0
                && !tab.IsAudioPanScriptInjected))
        {
            return null;
        }

        try
        {
            return await frame.ExecuteScriptAsync(
                script ?? BuildTabAudioPanScript(tab.AudioPan, tab.AudioBoostDecibels));
        }
        catch
        {
            // frame の遷移中や破棄直前は失敗するため、次回の frame イベントで再適用する。
            return null;
        }
    }

    private static string BuildTabAudioPanBootstrapScript()
    {
        return TabAudioScriptService.BuildTabAudioPanBootstrapScript();
    }

    internal static string BuildTabAudioPanScript(TabAudioPan audioPan, int audioBoostDecibels = 0)
    {
        return TabAudioScriptService.BuildTabAudioPanScript(audioPan, audioBoostDecibels);
    }

    private static void RefreshTabAudioState(BrowserTab tab)
    {
        if (tab.IsNativeContentTab || GetTabWebView(tab)?.CoreWebView2 is not { } core)
        {
            tab.IsPlayingAudio = false;
            return;
        }

        tab.IsPlayingAudio = core.IsDocumentPlayingAudio;
    }

    private void RefreshTabMuteState(BrowserTab tab)
    {
        if (tab.IsNativeContentTab || GetTabWebView(tab)?.CoreWebView2 is not { } core)
        {
            return;
        }

        var isMuted = core.IsMuted;
        if (tab.IsMuted == isMuted)
        {
            return;
        }

        tab.IsMuted = isMuted;
        QueueSessionSave();
    }

    private void QueueXReadableModeAttempt(BrowserTab tab)
    {
        _ = ApplyXReadableModeAsync(tab);
    }

    private void QueueChatGptReadabilityAttempt(BrowserTab tab)
    {
        _ = ApplyChatGptReadabilityAsync(tab);
    }

    private void ApplyChatGptReadabilityToLoadedTabs()
    {
        foreach (var tab in _tabs.ToList())
        {
            _ = ApplyChatGptReadabilityAsync(tab);
        }
    }

    private async Task ApplyChatGptReadabilityAsync(BrowserTab tab)
    {
        if (!_tabs.Contains(tab)
            || tab.IsNativeContentTab
            || GetTabWebView(tab)?.CoreWebView2 is not { } core
            || !_readabilityController.IsChatGptUrl(GetCurrentPageUrl(tab)))
        {
            return;
        }

        try
        {
            _settings.ChatGptReadability = ChatGptReadabilityProfile.Normalize(_settings.ChatGptReadability);
            await core.ExecuteScriptAsync(
                _readabilityController.BuildInjectReadabilityStyleScript(_settings.ChatGptReadability));
        }
        catch
        {
            // ページ遷移直後など実行できない場合は、次回の NavigationCompleted / SourceChanged に任せます。
        }
    }

    private void InitializeChatGptReadabilityPanel()
    {
        _chatGptReadabilityPanelController = new ChatGptReadabilityPanelController(
            new ChatGptReadabilityPanelElements(
                ChatGptReadabilityEnabledCheckBox,
                ChatGptFontScaleBox,
                ChatGptLineHeightBox,
                ChatGptThemeBox,
                ChatGptContentWidthBox,
                ChatGptCodeFontScaleBox,
                ChatGptCodeWrapCheckBox,
                ChatGptLargeInputCheckBox,
                ChatGptFocusHighlightCheckBox),
            this);
        _chatGptReadabilityPanelController.Initialize();
    }

    private void SyncChatGptReadabilityPanel()
    {
        _chatGptReadabilityPanelController.Sync();
    }

    private void SaveChatGptReadabilitySettingsFromPanel()
    {
        _chatGptReadabilityPanelController.SaveFromPanel();
    }

    ChatGptReadabilitySettings IChatGptReadabilityPanelHost.Settings
    {
        get => _settings.ChatGptReadability;
        set => _settings.ChatGptReadability = value;
    }

    void IChatGptReadabilityPanelHost.SaveSettings() => SettingsService.Save(_settings);
    void IChatGptReadabilityPanelHost.ApplyToLoadedTabs() => ApplyChatGptReadabilityToLoadedTabs();
    void IChatGptReadabilityPanelHost.SetStatus(string message) => SetStatus(message, autoClear: true);

    private bool IsActiveChatGptTab()
    {
        return ActiveTab is { IsNativeContentTab: false } activeTab
            && _readabilityController.IsChatGptUrl(GetCurrentPageUrl(activeTab));
    }

    private void ApplyXReadableModeToLoadedTabs()
    {
        foreach (var tab in _tabs.ToList())
        {
            _ = ApplyXReadableModeAsync(tab);
        }
    }

    private async Task ApplyXReadableModeAsync(BrowserTab tab)
    {
        if (!_tabs.Contains(tab)
            || tab.IsNativeContentTab
            || GetTabWebView(tab)?.CoreWebView2 is not { } core
            || !IsXOrTwitterUrl(GetCurrentPageUrl(tab)))
        {
            return;
        }

        try
        {
            await core.ExecuteScriptAsync(BuildXReadableModeScript(_settings.EnableXReadableMode));
        }
        catch
        {
            // ページ遷移直後など実行できない場合は、次回の NavigationCompleted / SourceChanged に任せます。
        }
    }

    private async Task ApplyYouTubeBestQualityForActiveTabAsync(bool showStatus)
    {
        if (!TryGetActiveYouTubeTab(out var tab))
        {
            SetStatus("YouTube 動画ページでのみ使えます", autoClear: true);
            return;
        }

        await ApplyYouTubeBestQualityForTabAsync(tab, showStatus);
    }

    private async Task ApplyYouTubeBestQualityForTabAsync(BrowserTab tab, bool showStatus)
    {
        await _youTubeControlService.ApplyBestQualityForTabAsync(tab, showStatus);
    }

    private void QueueYouTubePlaybackStateBridgeAttempt(BrowserTab tab)
    {
        _youTubeControlService.QueuePlaybackStateBridgeAttempt(tab);
    }

    private async Task CaptureYouTubePlaybackStateForLoadedTabsAsync(
        bool queueSaveOnChange,
        bool activeOnly = true)
    {
        try
        {
            await _youTubeControlService.CapturePlaybackStateForLoadedTabsAsync(
                queueSaveOnChange,
                activeOnly);
        }
        catch (Exception ex)
        {
            AppendSessionDiagnostic(
                "youtube-state-periodic-capture-failed",
                ex,
                $"window={_workspaceWindowIndex}");
        }
    }

    private async Task<bool> CaptureYouTubePlaybackStateAsync(BrowserTab tab)
    {
        return await _youTubeControlService.CapturePlaybackStateAsync(tab);
    }

    private bool ApplyYouTubePlaybackStateMessage(BrowserTab tab, JsonElement root)
    {
        var changed = _youTubeControlService.ApplyPlaybackStateMessage(tab, root);
        return changed;
    }

    private void QueueYouTubePlaybackRestoreAttempt(BrowserTab tab)
    {
        _youTubeControlService.QueuePlaybackRestoreAttempt(tab);
    }

    private void UpdateYouTubePlaybackStateTracking(BrowserTab? activeTab)
    {
        var nextActiveTab = IsYouTubeVideoTab(activeTab) ? activeTab : null;
        var nextActiveTabId = nextActiveTab?.Id;
        var transition = _youTubeControlWorkCoordinator.UpdatePlaybackStateTracking(
            nextActiveTabId,
            !_isClosingWindow && nextActiveTab is not null);
        if (transition.DeactivatedTabId is { } previousTabId
            && _tabs.FirstOrDefault(tab => tab.Id == previousTabId) is { } previousTab)
        {
            _ = _youTubeControlService.SetPlaybackStateBridgeActiveAsync(previousTab, false);
        }

        if (transition.ActivatedTabId is not null && nextActiveTab is not null)
        {
            _ = _youTubeControlService.SetPlaybackStateBridgeActiveAsync(nextActiveTab, true);
        }

        _youtubePlaybackStateCaptureTimer.Stop();
        if (transition.ShouldScheduleFallbackCapture)
        {
            _youtubePlaybackStateCaptureTimer.Start();
        }

        ResumeDeferredAutomaticBackgroundWorkIfPlaybackStopped();
    }

    private void CancelYouTubePlaybackStateFallback(BrowserTab tab)
    {
        if (!_youTubeControlWorkCoordinator.TryCancelPlaybackStateFallback(tab.Id))
        {
            return;
        }

        _youtubePlaybackStateCaptureTimer.Stop();
    }

    private void QueueYouTubePlaybackStateSave(string? reason)
    {
        var disposition = _youTubeControlWorkCoordinator.QueuePlaybackStateSave(
            reason,
            !_isClosingWindow && !_isPrivateWindow);
        if (disposition == YouTubePlaybackStateSaveDisposition.Ignored)
        {
            return;
        }

        if (disposition == YouTubePlaybackStateSaveDisposition.Deferred)
        {
            if (!_youtubePlaybackProgressSaveTimer.IsEnabled)
            {
                _youtubePlaybackProgressSaveTimer.Start();
            }

            return;
        }

        _youtubePlaybackProgressSaveTimer.Stop();
        QueueSessionSave();
    }

    private async Task ChangeYouTubePlaybackSpeedAsync(double delta)
    {
        if (!TryGetActiveYouTubeTab(out var tab))
        {
            SetStatus("YouTube 動画ページでのみ使えます", autoClear: true);
            return;
        }

        await _youTubeControlService.ChangePlaybackSpeedAsync(tab, delta);
    }

    private async Task ChangeYouTubePlaybackSpeedForPaneAsync(string pane, double delta)
    {
        if (!TryGetSplitPaneYouTubeTab(pane, out var tab))
        {
            SetStatus("指定した分割ペインに操作できる YouTube 動画ページがありません", autoClear: true);
            return;
        }

        ActivateSplitPane(pane);
        await _youTubeControlService.ChangePlaybackSpeedAsync(tab, delta);
    }

    private async Task ToggleYouTubePlaybackSpeedAsync()
    {
        if (!TryGetActiveYouTubeTab(out var tab))
        {
            SetStatus("YouTube 動画ページでのみ使えます", autoClear: true);
            return;
        }

        var targetSpeed = YouTubeControlScriptService.ResolvePlaybackSpeedToggleTarget(
            tab.YouTubePlaybackSpeed,
            tab.PreviousYouTubePlaybackSpeed,
            out var previousSpeed);
        tab.PreviousYouTubePlaybackSpeed = previousSpeed;
        await _youTubeControlService.SetPlaybackSpeedAsync(tab, targetSpeed);
    }

    private async Task ToggleYouTubePlaybackSpeedForPaneAsync(string pane)
    {
        if (!TryGetSplitPaneYouTubeTab(pane, out var tab))
        {
            SetStatus("指定した分割ペインに操作できる YouTube 動画ページがありません", autoClear: true);
            return;
        }

        ActivateSplitPane(pane);
        var targetSpeed = YouTubeControlScriptService.ResolvePlaybackSpeedToggleTarget(
            tab.YouTubePlaybackSpeed,
            tab.PreviousYouTubePlaybackSpeed,
            out var previousSpeed);
        tab.PreviousYouTubePlaybackSpeed = previousSpeed;
        await _youTubeControlService.SetPlaybackSpeedAsync(tab, targetSpeed);
    }

    private async Task ToggleYouTubePlaybackLoopAsync()
    {
        if (!TryGetActiveYouTubeTab(out var tab))
        {
            SetStatus("YouTube 動画ページでのみ使えます", autoClear: true);
            return;
        }

        await _youTubeControlService.TogglePlaybackLoopAsync(tab);
    }

    private async Task ToggleYouTubePlaybackLoopForPaneAsync(string pane)
    {
        if (!TryGetSplitPaneYouTubeTab(pane, out var tab))
        {
            SetStatus("指定した分割ペインに操作できる YouTube 動画ページがありません", autoClear: true);
            return;
        }

        ActivateSplitPane(pane);
        await _youTubeControlService.TogglePlaybackLoopAsync(tab);
    }

    bool IYouTubeControlPanelHost.IsActive => IsActive;
    bool IYouTubeControlPanelHost.IsClosing => _isClosingWindow;
    BrowserTab? IYouTubeControlPanelHost.ActiveTab => ActiveTab;
    bool IYouTubeControlPanelHost.IsSplitViewVisible() => IsSplitViewVisible();
    bool IYouTubeControlPanelHost.TryGetActiveYouTubeTab(out BrowserTab tab) => TryGetActiveYouTubeTab(out tab);
    bool IYouTubeControlPanelHost.TryGetSplitPaneYouTubeTab(string pane, out BrowserTab tab) => TryGetSplitPaneYouTubeTab(pane, out tab);
    BrowserTab? IYouTubeControlPanelHost.GetTabForPane(string pane) => _splitViewService.GetTabForPane(_tabs, pane);
    bool IYouTubeControlPanelHost.ContainsSplitTab(BrowserTab tab) => _splitViewService.Contains(tab);
    bool IYouTubeControlPanelHost.IsYouTubeVideoTab(BrowserTab? tab) => IsYouTubeVideoTab(tab);
    bool IYouTubeControlPanelHost.IsYouTubeSpeedDisplayTarget(BrowserTab tab) => IsYouTubeSpeedDisplayTarget(tab);
    Task<(double HorizontalOffset, double VerticalOffset)?> IYouTubeControlPanelHost.GetPlacementAsync(
        BrowserTab tab, FrameworkElement host, FrameworkElement panel)
        => TryGetYouTubeControlPopupPlacementAsync(tab, host, panel);
    Task IYouTubeControlPanelHost.RefreshControlDisplayAsync(BrowserTab tab)
        => _youTubeControlService.RefreshControlDisplayAsync(tab);
    Point IYouTubeControlPanelHost.GetElementOriginInWebHost(FrameworkElement element)
        => GetElementOriginInWebHost(element);
    void IYouTubeControlPanelHost.AppendSplitYouTubeControlDiagnostic(
        string operation, string reason, string pane, BrowserTab? tab,
        Popup popup, FrameworkElement host, string result)
        => AppendSplitYouTubeControlDiagnostic(operation, reason, pane, tab, popup, host, result);
    string IYouTubeControlPanelHost.GetYouTubeControlIneligibleReason(BrowserTab? tab)
        => GetYouTubeControlIneligibleReason(tab);

    private void UpdateYouTubeSpeedDisplay(double? speed)
        => _youTubeControlPanelController.UpdateYouTubeSpeedDisplay(speed);

    private void UpdateYouTubeSpeedDisplay(BrowserTab? tab, double? speed)
        => _youTubeControlPanelController.UpdateYouTubeSpeedDisplay(tab, speed);

    private static void SetYouTubeSpeedResetContent(Button button, double? speed)
        => YouTubeControlPanelPresentation.SetYouTubeSpeedResetContent(button, speed);

    private void UpdateYouTubeLoopDisplay(bool? isLooping)
        => _youTubeControlPanelController.UpdateYouTubeLoopDisplay(isLooping);

    private void UpdateYouTubeLoopDisplay(BrowserTab? tab, bool? isLooping)
        => _youTubeControlPanelController.UpdateYouTubeLoopDisplay(tab, isLooping);

    private static void SetYouTubeLoopContent(Button button, bool? isLooping)
        => YouTubeControlPanelPresentation.SetYouTubeLoopContent(button, isLooping);

    private void UpdateYouTubeControlPanelState(bool canControlYouTube)
        => _youTubeControlPanelController.UpdateYouTubeControlPanelState(canControlYouTube);

    private void UpdateYouTubeControlPopupPlacement()
        => _youTubeControlPanelController.UpdateYouTubeControlPopupPlacement();

    private void QueueYouTubeControlPlacementAfterWindowChange()
        => _youTubeControlPanelController.QueueYouTubeControlPlacementAfterWindowChange();

    private void RepositionOpenYouTubeControlPopupsAfterWindowChange()
        => _youTubeControlPanelController.RepositionOpenYouTubeControlPopupsAfterWindowChange();

    private bool UpdateSplitYouTubeControlPanels(string reason, bool allowInactiveWindow = false)
        => _youTubeControlPanelController.UpdateSplitYouTubeControlPanels(reason, allowInactiveWindow);

    private async Task<(double HorizontalOffset, double VerticalOffset)?> TryGetYouTubeControlPopupPlacementAsync(
        BrowserTab tab,
        FrameworkElement host,
        FrameworkElement panel)
    {
        var placement = await TryGetYouTubeControlPopupPlacementCoreAsync(tab, host, panel);
        return placement;
    }

    private async Task<(double HorizontalOffset, double VerticalOffset)?> TryGetYouTubeControlPopupPlacementCoreAsync(
        BrowserTab tab,
        FrameworkElement host,
        FrameworkElement panel)
    {
        if (GetTabWebView(tab)?.CoreWebView2 is not { } core)
        {
            return null;
        }

        return await TryGetYouTubeControlPopupPlacementCoreAsync(
            core,
            host,
            panel,
            GetTabWebMessageToken(tab),
            respectMouseActivity: true);
    }

    private async Task<(double HorizontalOffset, double VerticalOffset)?> TryGetYouTubeControlPopupPlacementCoreAsync(
        CoreWebView2 core,
        FrameworkElement host,
        FrameworkElement panel,
        bool respectMouseActivity,
        Point? hostOriginOverride = null)
    {
        return await TryGetYouTubeControlPopupPlacementCoreAsync(
            core,
            host,
            panel,
            webMessageToken: null,
            respectMouseActivity,
            hostOriginOverride);
    }

    private async Task<(double HorizontalOffset, double VerticalOffset)?> TryGetYouTubeControlPopupPlacementCoreAsync(
        CoreWebView2 core,
        FrameworkElement host,
        FrameworkElement panel,
        string? webMessageToken,
        bool respectMouseActivity,
        Point? hostOriginOverride = null)
    {
        string resultJson;
        try
        {
            resultJson = await core.ExecuteScriptAsync(
                YouTubeControlScriptService.BuildPlayerRectScript(webMessageToken));
        }
        catch
        {
            return null;
        }

        if (!TryReadScriptDouble(resultJson, "left", out var left)
            || !TryReadScriptDouble(resultJson, "top", out var top)
            || !TryReadScriptDouble(resultJson, "width", out var width)
            || !TryReadScriptDouble(resultJson, "height", out var height)
            || !TryReadScriptDouble(resultJson, "viewportWidth", out var viewportWidth)
            || !TryReadScriptDouble(resultJson, "viewportHeight", out var viewportHeight)
            || !TryReadScriptDouble(resultJson, "visibleWidth", out var visibleWidth)
            || !TryReadScriptDouble(resultJson, "visibleHeight", out var visibleHeight)
            || !TryReadScriptDouble(resultJson, "lastMouseActivityAgeMilliseconds", out var lastMouseActivityAgeMilliseconds)
            || !TryReadScriptBoolean(resultJson, "isMouseOverPlayer", out var isMouseOverPlayer)
            || viewportWidth <= 0
            || viewportHeight <= 0
            || width <= 0
            || height <= 0
            || visibleWidth <= 0
            || visibleHeight <= 0)
        {
            return null;
        }

        if (respectMouseActivity)
        {
            var hasRecentPanelInteraction = HasRecentYouTubeControlPanelInteraction();
            if ((!isMouseOverPlayer && !hasRecentPanelInteraction)
                || (lastMouseActivityAgeMilliseconds > YouTubeControlIdleHideDelayMilliseconds && !hasRecentPanelInteraction))
            {
                return null;
            }
        }

        var hostWidth = Math.Max(1, host.ActualWidth);
        var hostHeight = Math.Max(1, host.ActualHeight);
        var hostOrigin = hostOriginOverride ?? GetElementOriginInWebHost(host);
        var scaleX = hostWidth / viewportWidth;
        var scaleY = hostHeight / viewportHeight;
        var playerLeft = left * scaleX;
        var playerTop = top * scaleY;
        var playerRight = (left + width) * scaleX;
        var playerBottom = (top + height) * scaleY;
        var visibleLeft = Math.Max(0, playerLeft);
        var visibleRight = Math.Min(hostWidth, playerRight);
        var visibleTop = Math.Max(0, playerTop);
        var visibleBottom = Math.Min(hostHeight, playerBottom);

        if (visibleRight <= visibleLeft || visibleBottom <= visibleTop)
        {
            return null;
        }

        var panelSize = GetYouTubeControlPanelSize(panel);
        var bottomMargin = _youTubeControlWorkCoordinator.IsApplicationFullscreenActive
            ? YouTubeControlPopupFullscreenBottomMargin
            : YouTubeControlPopupBottomMargin;
        return (
            hostOrigin.X + (visibleLeft + visibleRight - panelSize.Width) / 2,
            hostOrigin.Y + visibleBottom - panelSize.Height - bottomMargin);
    }

    private Point GetElementOriginInWebHost(FrameworkElement element)
    {
        if (ReferenceEquals(element, WebHost))
        {
            return new Point(0, 0);
        }

        try
        {
            return element.TransformToAncestor(WebHost).Transform(new Point(0, 0));
        }
        catch
        {
            return new Point(0, 0);
        }
    }
    private void AppendSplitYouTubeControlDiagnostic(
        string operation,
        string reason,
        string pane,
        BrowserTab? tab,
        Popup popup,
        FrameworkElement host,
        string result)
    {
        if (_isPrivateWindow || IsRightWheelTabCyclingActive())
        {
            return;
        }

        if (string.Equals(reason, "timer", StringComparison.Ordinal))
        {
            return;
        }

        var origin = GetElementOriginInWebHost(host);
        var fields = new List<string>
        {
            "runner=split-youtube",
            $"operation={NormalizeSessionDiagnosticToken(operation)}",
            $"window={_workspaceWindowIndex}",
            $"reason={NormalizeSessionDiagnosticToken(reason)}",
            $"pane={NormalizeSessionDiagnosticToken(pane)}",
            $"result={NormalizeSessionDiagnosticToken(result)}",
            $"active={_splitViewService.IsActive}",
            $"visible={SplitViewHost.Visibility}",
            $"activePane={NormalizeSessionDiagnosticToken(_splitViewService.ActiveSplitView?.ActivePane ?? string.Empty)}",
            $"webHost={FormatDiagnosticSize(WebHost.ActualWidth, WebHost.ActualHeight)}",
            $"host={FormatDiagnosticSize(host.ActualWidth, host.ActualHeight)}",
            $"origin={FormatDiagnosticSize(origin.X, origin.Y)}",
            $"popupOpen={popup.IsOpen}",
            $"popupOffset={FormatDiagnosticSize(popup.HorizontalOffset, popup.VerticalOffset)}"
        };

        if (tab is not null)
        {
            fields.Add(BuildSplitTabDiagnostic("tab", tab));
            fields.Add($"currentUrl={NormalizeSessionDiagnosticToken(SummarizeOptionalDiagnosticUrl(GetCurrentPageUrl(tab)))}");
            fields.Add($"tabUrl={NormalizeSessionDiagnosticToken(SummarizeOptionalDiagnosticUrl(tab.Url))}");
        }

        AppendDiagnosticLogLine(fields.ToArray());
    }

    private string GetYouTubeControlIneligibleReason(BrowserTab? tab)
    {
        if (tab is null)
        {
            return "no-tab";
        }

        if (tab.IsNativeContentTab)
        {
            return "native-content-tab";
        }

        if (GetTabWebView(tab)?.CoreWebView2 is null)
        {
            return "no-core";
        }

        var currentUrl = GetCurrentPageUrl(tab);
        if (currentUrl is { } url && IsYouTubeVideoUrl(url))
        {
            return "eligible-current-url";
        }

        if (tab.Url is { } fallbackUrl && IsYouTubeVideoUrl(fallbackUrl))
        {
            return "eligible-tab-url";
        }

        if ((currentUrl is { } current && IsYouTubeUrl(current))
            || (tab.Url is { } fallback && IsYouTubeUrl(fallback)))
        {
            return "youtube-non-video";
        }

        return "non-youtube";
    }

    private static string SummarizeOptionalDiagnosticUrl(string? url)
    {
        return string.IsNullOrWhiteSpace(url)
            ? string.Empty
            : SummarizeDiagnosticUrl(url);
    }

    private void HideSplitYouTubeControlPopups()
        => _youTubeControlPanelController.HideSplitYouTubeControlPopups();

    private void SuspendYouTubeControlPopupsForInactiveWindow()
        => _youTubeControlPanelController.SuspendYouTubeControlPopupsForInactiveWindow();

    private void HandleYouTubeControlActivity(BrowserTab sourceTab, string? reason)
        => _youTubeControlPanelController.HandleYouTubeControlActivity(sourceTab, reason);

    private void ResetYouTubeControlSuppressionAfterNavigation(BrowserTab sourceTab)
        => _youTubeControlPanelController.ResetYouTubeControlSuppressionAfterNavigation(sourceTab);

    private bool HasRecentYouTubeControlPanelInteraction()
        => _youTubeControlPanelController.HasRecentYouTubeControlPanelInteraction();

    private static (double Width, double Height) GetYouTubeControlPanelSize(FrameworkElement panel)
        => YouTubeControlPanelPresentation.GetYouTubeControlPanelSize(panel);

    private async Task EnterYouTubeFullscreenAsync()
    {
        if (!TryGetActiveYouTubeTab(out var tab))
        {
            SetStatus("YouTube 動画ページでのみ使えます", autoClear: true);
            return;
        }

        ApplyYouTubeApplicationFullscreen(enterFullscreen: true);
        await _youTubeControlService.RequestFullscreenAsync(tab);
        SetStatus("YouTube 全画面を要求しました。Esc で戻ります", autoClear: true);
    }

    private void QueueYouTubeApplicationFullscreenExit()
    {
        _windowTaskSupervisor.TryRun(
            "youtube-application-fullscreen-exit",
            ExitYouTubeFullscreenAsync);
    }

    private async Task ExitYouTubeFullscreenAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (GetTabWebView(ActiveTab)?.CoreWebView2 is { } core)
        {
            await TryExitDocumentFullscreenAsync(core);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }

        ApplyYouTubeApplicationFullscreen(enterFullscreen: false);
    }

    private void RestoreApplicationFullscreenBeforeTabSelectionChange()
    {
        if (!_youTubeControlWorkCoordinator.IsApplicationFullscreenActive)
        {
            return;
        }

        if (GetTabWebView(ActiveTab)?.CoreWebView2 is { } core)
        {
            _ = TryExitDocumentFullscreenAsync(core);
        }

        ApplyYouTubeApplicationFullscreen(enterFullscreen: false);
    }

    private static async Task TryExitDocumentFullscreenAsync(CoreWebView2 core)
    {
        try
        {
            await core.ExecuteScriptAsync("""
                (() => {
                    if (document.fullscreenElement && document.exitFullscreen) {
                        document.exitFullscreen();
                    }
                })();
                """);
        }
        catch
        {
            // 全画面解除は補助操作なので、失敗してもアプリ側の復帰を優先します。
        }
    }

    private void ApplyYouTubeApplicationFullscreen(bool enterFullscreen)
    {
        if (enterFullscreen)
        {
            var snapshot = new YouTubeApplicationFullscreenRestoreSnapshot(
                (int)WindowStyle,
                (int)WindowState,
                (int)ResizeMode,
                Topmost,
                Left,
                Top,
                Width,
                Height);
            if (!_youTubeControlWorkCoordinator.TryBeginApplicationFullscreen(snapshot))
            {
                return;
            }

            _topToolbarAutoHideTimer.Stop();
            RootGrid.RowDefinitions[0].Height = new GridLength(0);
            RootGrid.RowDefinitions[1].Height = new GridLength(0);
            WindowBottomResizeRow.Height = new GridLength(0);
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            WindowState = WindowState.Maximized;
            Dispatcher.BeginInvoke(new Action(UpdateYouTubeControlPopupPlacement), DispatcherPriority.Loaded);
            return;
        }

        if (!_youTubeControlWorkCoordinator.TryEndApplicationFullscreen(out var restoreSnapshot))
        {
            return;
        }

        RootGrid.RowDefinitions[0].Height = GridLength.Auto;
        RootGrid.RowDefinitions[1].Height = new GridLength(40);
        WindowState = WindowState.Normal;
        WindowStyle = (WindowStyle)restoreSnapshot.WindowStyle;
        ResizeMode = (ResizeMode)restoreSnapshot.ResizeMode;
        Topmost = restoreSnapshot.Topmost;
        Left = restoreSnapshot.Left;
        Top = restoreSnapshot.Top;
        Width = restoreSnapshot.Width;
        Height = restoreSnapshot.Height;
        WindowState = (WindowState)restoreSnapshot.WindowState;
        UpdateWindowBottomResizeStrip();
        _statusPresentationController.UpdatePopupPlacement();
        Dispatcher.BeginInvoke(new Action(UpdateYouTubeControlPopupPlacement), DispatcherPriority.Loaded);
    }

    private bool TryGetActiveYouTubeTab(out BrowserTab tab)
    {
        if (IsYouTubeVideoTab(ActiveTab))
        {
            tab = ActiveTab!;
            return true;
        }

        tab = default!;
        return false;
    }

    private bool TryGetSplitPaneYouTubeTab(string pane, out BrowserTab tab)
    {
        var paneTab = IsSplitViewVisible()
            ? _splitViewService.GetTabForPane(_tabs, pane)
            : null;
        if (IsYouTubeVideoTab(paneTab))
        {
            tab = paneTab!;
            return true;
        }

        tab = default!;
        return false;
    }

    private bool IsYouTubeSpeedDisplayTarget(BrowserTab tab)
    {
        return ActiveTab?.Id == tab.Id || IsVisibleSplitYouTubeTab(tab);
    }

    private bool IsVisibleSplitYouTubeTab(BrowserTab tab)
    {
        return IsSplitViewVisible()
            && _splitViewService.Contains(tab)
            && IsYouTubeVideoTab(tab);
    }

    private static bool IsTabAudioBoostEligible(BrowserTab? tab)
    {
        return tab is { IsNativeContentTab: false }
            && GetTabWebView(tab)?.CoreWebView2 is not null;
    }

    private bool IsSplitViewVisible()
    {
        return _splitViewService.IsActive && SplitViewHost.Visibility == Visibility.Visible;
    }

    private bool IsYouTubeVideoTab(BrowserTab? tab)
    {
        if (tab is not { IsNativeContentTab: false }
            || GetTabWebView(tab)?.CoreWebView2 is null)
        {
            return false;
        }

        var currentUrl = GetCurrentPageUrl(tab);
        return currentUrl is { } url && IsYouTubeVideoUrl(url)
            || tab.Url is { } fallbackUrl && IsYouTubeVideoUrl(fallbackUrl);
    }

    private async Task<string?> ExecuteYouTubeScriptAsync(BrowserTab tab, string script, bool showStatus)
    {
        if (_webViewLifecycleService.HasLoadFailure(tab)
            || TryGetCoreWebView2(tab) is not { } core)
        {
            if (showStatus)
            {
                SetStatus("YouTube 操作できるページがありません", autoClear: true);
            }

            return null;
        }

        try
        {
            return await core.ExecuteScriptAsync(script);
        }
        catch (Exception ex)
        {
            if (showStatus)
            {
                SetStatus("YouTube 操作に失敗しました: " + ex.Message, autoClear: true);
            }

            return null;
        }
    }

    private void QueueTabFaviconRefresh(BrowserTab tab)
    {
        _windowTaskSupervisor.TryRun(
            "tab-favicon-refresh",
            cancellationToken => UpdateTabFaviconAsync(tab, cancellationToken));
    }

    private async Task UpdateTabFaviconAsync(BrowserTab tab, CancellationToken cancellationToken)
    {
        if (!_tabs.Contains(tab) || GetTabWebView(tab)?.CoreWebView2 is not { } core)
        {
            return;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(core.FaviconUri))
            {
                _ = s_tabRuntimeCoordinator.SetFavicon(tab, null);
                tab.FaviconPngBase64 = string.Empty;
                QueueWorkspaceOpenTabsRefresh();
                return;
            }

            await using var faviconStream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            cancellationToken.ThrowIfCancellationRequested();
            if (faviconStream is null)
            {
                return;
            }

            await using var buffer = new MemoryStream();
            await faviconStream.CopyToAsync(buffer, cancellationToken);
            var bytes = buffer.ToArray();
            if (bytes.Length == 0 || bytes.Length > 256 * 1024)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            _ = s_tabRuntimeCoordinator.SetFavicon(tab, CreateImageSource(bytes, decodePixelWidth: 24));
            tab.FaviconPngBase64 = Convert.ToBase64String(bytes);
            QueueWorkspaceOpenTabsRefresh();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            // favicon は補助情報なので、取得できなくてもタブ表示は維持します。
        }
    }

    private void RefreshTabFromCore(BrowserTab tab)
    {
        if (IsNewTabUrl(tab.Url))
        {
            tab.Title = "新しいタブ";
            if (ActiveTab == tab)
            {
                SetAddressText(string.Empty);
            }

            return;
        }

        if (IsUserGuideUrl(tab.Url))
        {
            tab.Title = "ユーザーガイド";
            if (ActiveTab == tab)
            {
                SetAddressText(UserGuideUrl);
            }

            return;
        }

        var source = GetTabWebView(tab)?.CoreWebView2?.Source;
        if (!string.IsNullOrWhiteSpace(source) && !IsBlankUrl(source))
        {
            tab.Url = source;
            if (ActiveTab == tab)
            {
                SetAddressText(source);
            }
        }

        if (GetTabWebView(tab)?.CoreWebView2 is { DocumentTitle.Length: > 0 } core)
        {
            tab.Title = core.DocumentTitle;
        }
    }

    private static void RestoreNavigationHistory(
        BrowserTab tab, IEnumerable<string>? history, int historyIndex, string? fallbackUrl)
        => s_tabNavigationHistoryService.RestoreNavigationHistory(tab, history, historyIndex, fallbackUrl);

    private void RecordTabNavigation(BrowserTab tab, string? url)
        => s_tabNavigationHistoryService.RecordTabNavigation(tab, url, QueueSessionSave);

    private static bool TryCompleteSavedNavigationHistory(BrowserTab tab, string? finalUrl, bool isSuccess)
        => s_tabNavigationHistoryService.TryCompleteSavedNavigationHistory(tab, finalUrl, isSuccess);

    private static bool ShouldPreservePendingSavedNavigationHistory(
        BrowserTab tab, string? navigationUrl, bool isRedirected)
        => s_tabNavigationHistoryService.ShouldPreservePendingSavedNavigationHistory(tab, navigationUrl, isRedirected);

    private static List<string> CreatePersistedNavigationHistory(
        BrowserTab tab, string? currentUrl, out int historyIndex)
        => s_tabNavigationHistoryService.CreatePersistedNavigationHistory(tab, currentUrl, out historyIndex);

    private static bool IsPersistableNavigationHistoryUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || IsNewTabUrl(url)
            || IsUserGuideUrl(url)
            || IsBlankUrl(url)
            || IsGeneratedNewTabSource(url))
        {
            return false;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals(Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals("about", StringComparison.OrdinalIgnoreCase);
    }

    private static ImageSource? CreateImageSource(byte[] bytes, int decodePixelWidth)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = decodePixelWidth;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private void NavigateCore(
        BrowserTab tab,
        string url,
        bool replaceCurrentHistoryEntry = false)
    {
        if (tab.IsNativeContentTab)
        {
            return;
        }

        url = NormalizeYouTubeShortsUrl(url);
        var webView = GetTabWebView(tab);
        if (webView?.CoreWebView2 is null || IsBlankUrl(url))
        {
            return;
        }

        try
        {
            if (IsNewTabUrl(url))
            {
                tab.Title = "新しいタブ";
                AppendWebViewPerformanceDiagnostic(
                    "webview-navigate-request",
                    tab,
                    url,
                    detail: "newTab=true");
                webView.CoreWebView2.NavigateToString(BuildNewTabHtml(tab));
                return;
            }

            if (IsUserGuideUrl(url))
            {
                NavigateUserGuide(tab);
                return;
            }

            CloseBookmarkPreview(clearNavigation: true);
            var isVisibleSplitPaneNavigation = IsVisibleOrPreparingSplitPaneNavigation(tab);
            var isBackgroundNavigation = ActiveTab?.Id != tab.Id && !isVisibleSplitPaneNavigation;
            if (isBackgroundNavigation)
            {
                YouTubePlaybackStateService.MarkBackgroundNavigationPaused(tab, url);
            }

            var navigationUrl = YouTubePlaybackStateService.BuildNavigationUrl(tab, url, isBackgroundNavigation);
            tab.Url = url;
            AppendWebViewPerformanceDiagnostic(
                "webview-navigate-request",
                tab,
                navigationUrl,
                detail: $"background={isBackgroundNavigation};visibleSplit={isVisibleSplitPaneNavigation}");
            if (replaceCurrentHistoryEntry)
            {
                QueueYouTubeShortsHistoryReplaceNavigation(
                    tab,
                    webView.CoreWebView2,
                    navigationUrl);
            }
            else
            {
                webView.CoreWebView2.Navigate(navigationUrl);
            }
        }
        catch (Exception ex)
        {
            SetStatus("移動できません: " + ex.Message, autoClear: true);
        }
    }

    private void NavigateUserGuide(BrowserTab tab)
    {
        if (GetTabWebView(tab)?.CoreWebView2 is not { } core)
        {
            return;
        }

        tab.Url = UserGuideUrl;
        tab.Title = "ユーザーガイド";
        var guideDirectory = Path.Combine(AppContext.BaseDirectory, "Help");
        var guidePath = Path.Combine(guideDirectory, "user-guide.html");
        if (!File.Exists(guidePath))
        {
            core.NavigateToString(BuildUserGuideUnavailableHtml());
            return;
        }

        core.SetVirtualHostNameToFolderMapping(
            UserGuideVirtualHostName,
            guideDirectory,
            CoreWebView2HostResourceAccessKind.DenyCors);
        core.Navigate(UserGuideContentUrl);
    }

    private async Task OpenUserGuideAsync()
    {
        var tab = _tabs.FirstOrDefault(static tab => IsUserGuideUrl(tab.Url));
        if (tab is null)
        {
            tab = OpenTab(
                UserGuideUrl,
                activate: false,
                title: "ユーザーガイド",
                queueBackgroundLoading: false);
        }

        SelectTab(tab);
        await ActivateSelectedTabAsync();
        SetAddressText(UserGuideUrl);
    }

    private static string BuildUserGuideUnavailableHtml() => UserGuidePageHtmlBuilder.BuildUnavailable();

    private void HandleWebMessage(BrowserTab sourceTab, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            using var document = JsonDocument.Parse(args.WebMessageAsJson);
            var root = document.RootElement;
            if (!WebMessageValidationService.HasValidToken(root, GetTabWebMessageToken(sourceTab)))
            {
                return;
            }

            var type = GetJsonString(root, "type");
            if (StreamingPlaybackCompatibilityService.TryParse(
                    root,
                    out var streamingPlaybackFailure))
            {
                var pageUrl = GetCurrentPageUrl(sourceTab) ?? sourceTab.Url;
                if (StreamingPlaybackCompatibilityService.IsEligiblePageUrl(pageUrl)
                    && StreamingPlaybackCompatibilityService.IsTrustedTopLevelSource(
                        pageUrl,
                        args.Source))
                {
                    MarkStreamingPlaybackFailed(sourceTab, streamingPlaybackFailure);
                }

                return;
            }

            if (YouTubePlaybackDiagnosticService.TryParse(root, out var playbackDiagnostic))
            {
                if (YouTubePlaybackDiagnosticService.IsTrustedYouTubeSource(args.Source))
                {
                    HandleYouTubePlaybackDiagnostic(
                        "tab",
                        sourceTab.Id,
                        GetCurrentPageUrl(sourceTab) ?? sourceTab.Url,
                        playbackDiagnostic);
                }

                return;
            }

            if (WebMessageValidationService.RequiresInternalNewTab(type)
                && !IsNewTabUrl(sourceTab.Url))
            {
                return;
            }

            switch (type)
            {
                case AmazonPrimeVideoService.CompatibilityActiveMessageType:
                    MarkAmazonPrimeVideoCompatibilityActive(sourceTab, args.Source);
                    break;
                case StreamingPlaybackCompatibilityService.CompatibilityActiveMessageType:
                    MarkStreamingPlaybackCompatibilityActive(sourceTab, args.Source);
                    break;
                case "gestureVisualStart":
                    if (!TryClaimNativeWebGestureForPage(sourceTab))
                    {
                        if (GetTabWebView(sourceTab)?.CoreWebView2 is { } ignoredGestureCore)
                        {
                            _ = CancelActiveGestureOverlayAsync(ignoredGestureCore);
                        }

                        break;
                    }

                    var gestureInteraction = s_inputInteractionHub.TryBegin(
                        InputInteractionKind.PageGesture,
                        _workspaceWindowId,
                        sourceTab.Id,
                        DateTimeOffset.Now);
                    if (!gestureInteraction.Accepted)
                    {
                        ResetNativeWebGestureForTab(sourceTab);
                        if (GetTabWebView(sourceTab)?.CoreWebView2 is { } gestureCore)
                        {
                            _ = CancelActiveGestureOverlayAsync(gestureCore);
                        }

                        break;
                    }

                    BeginPageGestureVisual(
                        sourceTab,
                        GetJsonDouble(root, "x") ?? double.NaN,
                        GetJsonDouble(root, "y") ?? double.NaN);
                    break;
                case "gestureVisualState":
                    if (ShouldIgnorePageGestureMessage(sourceTab))
                    {
                        break;
                    }

                    var pageGestureRoute = GetJsonString(root, "route");
                    MarkNativeWebGesturePageRoute(sourceTab, pageGestureRoute);
                    UpdatePageGestureVisualState(
                        sourceTab,
                        pageGestureRoute,
                        GetJsonString(root, "status"));
                    break;
                case "gestureVisualEnd":
                    if (ShouldIgnorePageGestureMessage(sourceTab))
                    {
                        break;
                    }

                    s_inputInteractionHub.TryComplete(
                        InputInteractionKind.PageGesture,
                        _workspaceWindowId,
                        sourceTab.Id);
                    EndPageGestureVisual();
                    break;
                case "gesture":
                    if (ShouldIgnorePageGestureMessage(sourceTab))
                    {
                        return;
                    }

                    if (!WebMessageValidationService.IsConfiguredGesture(root, _settings.GestureMappings))
                    {
                        return;
                    }

                    s_inputInteractionHub.TryComplete(
                        InputInteractionKind.PageGesture,
                        _workspaceWindowId,
                        sourceTab.Id);
                    ResetNativeWebGestureForTab(sourceTab);
                    ActivateSplitPaneForTab(sourceTab);
                    QueueGestureCommandExecution(
                        GetJsonString(root, "command"),
                        GetJsonString(root, "selectedText"),
                        sourceTab,
                        GetJsonString(root, "linkUrl"),
                        GetJsonString(root, "linkText"));
                    break;
                case "openBackgroundTab":
                    var url = GetJsonString(root, "url");
                    if (!string.IsNullOrWhiteSpace(url) && IsHttpOrHttpsUrl(url))
                    {
                        OpenBackgroundTabNextTo(sourceTab, url);
                        SetStatus("リンクを右隣の裏タブで開きました", autoClear: true);
                    }
                    break;
                case "newTabSearch":
                    var query = GetJsonString(root, "query");
                    if (!string.IsNullOrWhiteSpace(query) && ActiveTab is { } searchTab)
                    {
                        var target = _addressService.Resolve(query);
                        QueueNewTabSearchNavigation(searchTab, target);
                    }
                    break;
                case "openBookmark":
                    var bookmarkId = GetJsonString(root, "id");
                    if (Guid.TryParse(bookmarkId, out var id)
                        && _bookmarks.FirstOrDefault(x => x.Id == id) is { } bookmark)
                    {
                        QueueBookmarkOpen(bookmark);
                    }
                    break;
                case "deleteBookmark":
                    var deleteBookmarkId = GetJsonString(root, "id");
                    if (Guid.TryParse(deleteBookmarkId, out var targetBookmarkId))
                    {
                        QueueBookmarkDelete(targetBookmarkId);
                    }
                    break;
                case "showBookmarkPreview":
                    QueueBookmarkPreviewShow(
                        GetJsonString(root, "url"),
                        GetJsonString(root, "title"),
                        GetJsonDouble(root, "left"),
                        GetJsonDouble(root, "top"),
                        GetJsonDouble(root, "right"),
                        GetJsonDouble(root, "bottom"));
                    break;
                case "hideBookmarkPreview":
                    CloseBookmarkPreview(clearNavigation: GetJsonBool(root, "clearNavigation"));
                    break;
                case "linkHover":
                    ScheduleLinkPreview(
                        sourceTab,
                        GetJsonString(root, "url"),
                        GetJsonString(root, "title"),
                        GetJsonDouble(root, "x"),
                        GetJsonDouble(root, "y"));
                    break;
                case "linkHoverEnd":
                    HandleLinkHoverEnd(sourceTab);
                    break;
                case "pageContextMenu":
                    HandlePageContextMenuMessage(sourceTab, root);
                    break;
                case "youtubeControlActivity":
                    if (YouTubePlaybackDiagnosticService.IsTrustedYouTubeSource(args.Source))
                    {
                        HandleYouTubeControlActivity(sourceTab, GetJsonString(root, "reason"));
                    }
                    break;
                case "youtubePlaybackState":
                    if (!IsYouTubeVideoTab(sourceTab))
                    {
                        return;
                    }

                    CancelYouTubePlaybackStateFallback(sourceTab);
                    var previousSpeed = sourceTab.YouTubePlaybackSpeed;
                    var previousLooping = sourceTab.YouTubePlaybackIsLooping;
                    var previousPaused = sourceTab.YouTubePlaybackIsPaused;
                    var playbackStateChanged = ApplyYouTubePlaybackStateMessage(sourceTab, root);
                    if (previousSpeed != sourceTab.YouTubePlaybackSpeed)
                    {
                        UpdateYouTubeSpeedDisplay(sourceTab, sourceTab.YouTubePlaybackSpeed);
                    }

                    if (previousLooping != sourceTab.YouTubePlaybackIsLooping)
                    {
                        UpdateYouTubeLoopDisplay(sourceTab, sourceTab.YouTubePlaybackIsLooping);
                    }

                    if (playbackStateChanged)
                    {
                        QueueYouTubePlaybackStateSave(GetJsonString(root, "reason"));
                    }

                    if (previousPaused != sourceTab.YouTubePlaybackIsPaused)
                    {
                        ResumeDeferredAutomaticBackgroundWorkIfPlaybackStopped();
                    }
                    break;
            }
        }
        catch
        {
            SetStatus("ページメッセージを処理できませんでした", autoClear: true);
        }
    }

    private Task ExecuteBrowserCommandAsync(string? command, string? selectedText)
    {
        return ExecuteBrowserCommandAsync(command, selectedText, targetId: null);
    }

    private async Task ExecuteBrowserCommandAsync(
        string? command,
        string? selectedText,
        Guid? targetId)
    {
        NoteExplorationActivity();
        var target = targetId is { } explicitTargetId
            ? _tabs.FirstOrDefault(tab => tab.Id == explicitTargetId)
            : GetActiveBrowserCommandTarget();

        switch (command)
        {
            case "Back":
                await NavigateBackAsync(target);
                break;
            case "Forward":
                await NavigateForwardAsync(target);
                break;
            case "NewTab":
                AddNewTab(activate: true);
                break;
            case "DuplicateTab":
                if (target is not null)
                {
                    DuplicateTab(target, activate: true);
                }
                break;
            case "DuplicateTabBackground":
                if (target is not null)
                {
                    DuplicateTab(target, activate: false);
                }
                break;
            case "CloseTab":
                if (target is { IsWorkspaceTab: false })
                {
                    CloseTab(target);
                }
                break;
            case "CloseOtherTabs":
                CloseOtherTabs(target);
                break;
            case "CloseTabsToRight":
                CloseTabsToSide(closeRight: true, target);
                break;
            case "CloseTabsToLeft":
                CloseTabsToSide(closeRight: false, target);
                break;
            case "ToggleTabMute":
                if (target is not null)
                {
                    ToggleTabMute(target);
                }
                break;
            case "ToggleTabPinned":
                if (target is not null)
                {
                    ToggleTabPinned(target);
                }
                break;
            case "Reload":
                await ReloadActiveTabAsync(target);
                break;
            case "StopLoading":
                if (target is { } stopTarget)
                {
                    GetTabWebView(stopTarget)?.CoreWebView2?.Stop();
                    _ = s_tabRuntimeCoordinator.SetLoading(stopTarget, false);
                }
                break;
            case "NextTab":
                BeginRightWheelTabCyclingIfNeeded();
                MoveTabSelection(1);
                break;
            case "PreviousTab":
                BeginRightWheelTabCyclingIfNeeded();
                MoveTabSelection(-1);
                break;
            case "RestoreClosedTab":
                RestoreRecentlyClosedItem();
                break;
            case "ScrollTop":
                await ExecuteScriptOnActiveTabAsync(
                    "window.scrollTo({ top: 0, behavior: 'auto' });",
                    target);
                break;
            case "ScrollBottom":
                await ExecuteScriptOnActiveTabAsync(
                    "window.scrollTo({ top: document.documentElement.scrollHeight || document.body.scrollHeight, behavior: 'auto' });",
                    target);
                break;
            case "ScrollPageUp":
                await ExecuteScriptOnActiveTabAsync(
                    "window.scrollBy({ top: -Math.max(window.innerHeight * 0.9, 1), behavior: 'auto' });",
                    target);
                break;
            case "ScrollPageDown":
                await ExecuteScriptOnActiveTabAsync(
                    "window.scrollBy({ top: Math.max(window.innerHeight * 0.9, 1), behavior: 'auto' });",
                    target);
                break;
            case "SearchSelection":
                OpenSelectedTextSearch(target, selectedText);
                break;
            case "CopyUrl":
                CopyActiveTabUrl(target);
                break;
            case "AddBookmark":
                await AddCurrentPageBookmarkAsync(target);
                break;
            case "ShowBookmarks":
                await ShowBookmarkManagerAsync();
                break;
            case "ShowWorkspace":
                OpenWorkspaceTab();
                break;
            case "ShowWorkspaceWindow":
                ShowWorkspaceOSWindow();
                break;
        }
    }

    private async Task NavigateBackAsync(
        BrowserTab? target = null,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if ((target ?? GetActiveBrowserCommandTarget()) is not { } tab)
        {
            return;
        }

        if (GetTabHostedContent(tab) is Views.FolderTabView folderTabView)
        {
            await folderTabView.NavigateBackAsync();
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            UpdateToolbarState();
            return;
        }

        if (tab.PreferSavedNavigationHistory)
        {
            await NavigateSavedNavigationHistoryAsync(tab, offset: -1, cancellationToken: cancellationToken);
            return;
        }

        if (GetTabWebView(tab)?.CoreWebView2 is { CanGoBack: true } backCore)
        {
            backCore.GoBack();
            return;
        }

        await NavigateSavedNavigationHistoryAsync(tab, offset: -1, cancellationToken: cancellationToken);
    }

    private async Task NavigateForwardAsync(
        BrowserTab? target = null,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if ((target ?? GetActiveBrowserCommandTarget()) is not { } tab)
        {
            return;
        }

        if (GetTabHostedContent(tab) is Views.FolderTabView folderTabView)
        {
            await folderTabView.NavigateForwardAsync();
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            UpdateToolbarState();
            return;
        }

        if (tab.PreferSavedNavigationHistory)
        {
            await NavigateSavedNavigationHistoryAsync(tab, offset: 1, cancellationToken: cancellationToken);
            return;
        }

        if (GetTabWebView(tab)?.CoreWebView2 is { CanGoForward: true } forwardCore)
        {
            forwardCore.GoForward();
            return;
        }

        await NavigateSavedNavigationHistoryAsync(tab, offset: 1, cancellationToken: cancellationToken);
    }

    private async Task NavigateSavedNavigationHistoryAsync(
        BrowserTab tab,
        int offset,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (!s_tabNavigationHistoryService.TryBeginSavedNavigationHistory(
            tab,
            offset,
            out var targetUrl))
        {
            return;
        }

        ResetWebViewLoadFailureForRetry(tab);
        await EnsureTabLoadedAsync(tab, null);
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        NavigateCore(tab, targetUrl);
        if (ActiveTab == tab)
        {
            SetAddressText(targetUrl);
        }

        UpdateToolbarState();
        QueueSessionSave();
    }

    private static bool CanNavigateBrowserHistory(BrowserTab? tab, int offset)
    {
        if (tab is null || offset is not (-1 or 1))
        {
            return false;
        }

        if (GetTabHostedContent(tab) is Views.FolderTabView folderTabView)
        {
            return offset < 0
                ? folderTabView.CanNavigateBack
                : folderTabView.CanNavigateForward;
        }

        if (tab.PreferSavedNavigationHistory)
        {
            return s_tabNavigationHistoryService.CanNavigateSavedNavigationHistory(tab, offset);
        }

        var core = GetTabWebView(tab)?.CoreWebView2;
        var canNavigateWebHistory = offset < 0
            ? core?.CanGoBack == true
            : core?.CanGoForward == true;
        return canNavigateWebHistory
            || s_tabNavigationHistoryService.CanNavigateSavedNavigationHistory(tab, offset);
    }

    private async Task ReloadActiveTabAsync(
        BrowserTab? target = null,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var tab = target ?? GetActiveBrowserCommandTarget();
        if (tab is null)
        {
            return;
        }

        if (GetTabHostedContent(tab) is Views.FolderTabView folderTabView)
        {
            await folderTabView.RefreshAsync();
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            UpdateToolbarState();
            return;
        }

        if (_webViewLifecycleService.HasLoadFailure(tab))
        {
            await RecoverWebViewProcessFailureAsync(tab);
            return;
        }

        if (GetTabWebView(tab)?.CoreWebView2 is { } core)
        {
            core.Reload();
            QueueWorkspaceOpenTabsRefresh();
            return;
        }

        if (!string.IsNullOrWhiteSpace(tab.Url))
        {
            ClearWebViewLoadFailure(tab);
            await EnsureTabLoadedAsync(tab, tab.Url);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            QueueWorkspaceOpenTabsRefresh();
        }
    }

    private async Task ExecuteScriptOnActiveTabAsync(
        string script,
        BrowserTab? target = null)
    {
        if (GetTabWebView(target ?? GetActiveBrowserCommandTarget())?.CoreWebView2 is { } core)
        {
            await core.ExecuteScriptAsync(script);
        }
    }

    private void ToggleTabPinned(BrowserTab tab)
    {
        if (!_tabs.Contains(tab) || tab.IsWorkspaceTab)
        {
            return;
        }

        tab.IsPinned = !tab.IsPinned;
        RefreshVerticalTabViews();
        QueueSessionSave();
    }

    private void DuplicateTab(BrowserTab tab, bool activate)
    {
        if (!_tabs.Contains(tab))
        {
            return;
        }

        if (tab.IsWorkspaceTab)
        {
            OpenWorkspaceTab();
            return;
        }

        if (tab.IsFolderTab
            && GetTabHostedContent(tab) is Views.FolderTabView folderTabView)
        {
            var folderPath = string.IsNullOrWhiteSpace(folderTabView.CurrentPath)
                ? tab.Url
                : folderTabView.CurrentPath;
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return;
            }

            OpenFolderInAdjacentTab(
                tab,
                folderTabView,
                folderPath,
                activate,
                folderTabView.CaptureSessionState());
            return;
        }

        var url = GetCurrentPageUrl(tab);
        if (string.IsNullOrWhiteSpace(url))
        {
            AddNewTab(activate);
            return;
        }

        var sourceIndex = _tabs.IndexOf(tab);
        var activeIndex = ActiveTab is { } activeTab ? _tabs.IndexOf(activeTab) : -1;
        var insertionIndex = _tabOperationService.GetAdjacentInsertionIndex(
            _tabs.Count,
            sourceIndex,
            activeIndex);
        var duplicatedTab = OpenTab(
            url,
            activate,
            GetCurrentPageTitle(tab, url),
            insertionIndex: insertionIndex,
            containerProfileId: tab.ContainerProfileId,
            queueBackgroundLoading: activate);
        if (!activate)
        {
            QueueUserRequestedBackgroundTabLoading(duplicatedTab);
        }
    }

    private void CloseOtherTabs(BrowserTab? target = null)
        => _closedTabLifecycleController.CloseOtherTabs(target);

    private void CloseTabsToSide(bool closeRight, BrowserTab? target = null)
        => _closedTabLifecycleController.CloseTabsToSide(closeRight, target);

    private void CopyActiveTabUrl(BrowserTab? target = null)
    {
        if ((target ?? GetActiveBrowserCommandTarget()) is not { } tab)
        {
            return;
        }

        var url = GetCurrentPageUrl(tab);
        if (string.IsNullOrWhiteSpace(url))
        {
            SetStatus("コピーできる URL がありません", autoClear: true);
            return;
        }

        try
        {
            Clipboard.SetText(url);
            SetStatus("URL をコピーしました", autoClear: true);
        }
        catch (Exception ex)
        {
            SetStatus("URL をコピーできません: " + ex.Message, autoClear: true);
        }
    }

    private async Task AddCurrentPageBookmarkAsync(BrowserTab? target = null)
    {
        await _bookmarkController.AddCurrentPageAsync(target);
    }

    private async Task ShowBookmarkManagerAsync()
    {
        if (!_bookmarkController.CanPersistChanges)
        {
            SetStatus(_bookmarkController.IsAvailable
                ? "ブックマーク本体を修復できないため、編集一覧を開けません"
                : "ブックマークを読み込めないため、一覧を開けません");
            return;
        }

        var dialog = new BookmarkManagerWindow(
            _bookmarks,
            _bookmarkFolders,
            _bookmarkController.SortMode,
            _settings.BookmarkManagerWindowSize)
        {
            Owner = this
        };

        var wasShown = await ModelessWindowService.ShowAsync(dialog);
        if (!wasShown || _isClosingWindow)
        {
            return;
        }

        if (dialog.ChangedBookmarks)
        {
            await _bookmarkController.ApplyManagerChangesAsync(dialog.CreateChangeSet());
        }

        if (dialog.IsAccepted
            && dialog.SelectedBookmark is { } selectedBookmark
            && _bookmarks.FirstOrDefault(item => item.Id == selectedBookmark.Id) is { } bookmark)
        {
            await OpenBookmarkAsync(bookmark);
        }

        if (dialog.CapturedWindowSize is { } windowSize)
        {
            _settings.BookmarkManagerWindowSize = windowSize;
            SettingsService.Save(_settings);
        }
    }

    private void OpenWorkspaceTab() => _workspaceTabController.Open();

    private void ToggleWorkspaceTab() => _workspaceTabController.Toggle();

    private BrowserTab EnsureWorkspaceTabOpen(bool activate) => _workspaceTabController.EnsureOpen(activate);

    IList<BrowserTab> IWorkspaceTabHost.Tabs => _tabs;
    BrowserTab? IWorkspaceTabHost.ActiveTab => ActiveTab;
    Guid? IWorkspaceTabHost.LastActiveBrowserTabId => _lastActiveBrowserTabId;
    void IWorkspaceTabHost.SelectTab(BrowserTab tab) => SelectTab(tab);
    void IWorkspaceTabHost.QueueSelectedTabActivation() => QueueSelectedTabActivation();
    void IWorkspaceTabHost.QueueSessionSave() => QueueSessionSave();
    void IWorkspaceTabHost.SetStatus(string message, bool autoClear) => SetStatus(message, autoClear);

    void IWorkspaceTabHost.MoveToFront(int index, bool animate)
    {
        if (animate)
        {
            var positions = CaptureTabLayoutPositions();
            _tabs.Move(index, 0);
            AnimateTabLayoutFrom(positions);
        }
        else
        {
            _tabs.Move(index, 0);
        }
    }

    void IWorkspaceTabHost.InsertAtFront(BrowserTab tab, bool animate)
    {
        if (animate)
        {
            var positions = CaptureTabLayoutPositions();
            _tabs.Insert(0, tab);
            AnimateTabLayoutFrom(positions);
        }
        else
        {
            _tabs.Insert(0, tab);
        }
    }

    private int ClampNormalTabInsertionIndex(int insertionIndex)
    {
        EnsureWorkspaceTabIsFirst();
        return _tabOperationService.ClampInsertionIndexAfterLeadingPinnedTab(
            insertionIndex,
            _tabs.Count,
            _tabs.FirstOrDefault() is { IsWorkspaceTab: true });
    }

    private void EnsureWorkspaceTabIsFirst() => _workspaceTabController.EnsureFirst();

    private UIElement CreateWorkspaceOSView(BrowserTab workspaceTab)
    {
        var view = new Views.WorkspaceOSView();
        var viewModel = new ViewModels.WorkspaceOSViewModel(
            new WorkspaceService(),
            _settings.LastWorkspaceId,
            new ViewModels.WorkspaceTabPort(
                OpenWorkspaceUrlAsync, OpenWorkspaceSessionTabsAsync,
                CaptureWorkspaceOpenTabs, CaptureWorkspaceCurrentTab,
                CloseWorkspaceTabAsync, OpenShellFolderInTab,
                () => _workspaceSessionController.DeferredRestoreMessage),
            new ViewModels.WorkspaceMiniPlayerPort(
                CaptureWorkspaceMediaOverlayShelfItems, CaptureWorkspaceMediaOverlaysAsync, OpenWorkspaceMediaSetAsync),
            new ViewModels.WorkspaceWindowPort(
                OpenWorkspaceSessionTabsInNewWindowAsync, OpenWorkspaceFolderWindow, SaveLastWorkspaceId,
                () => _workspaceSessionController.DeferredRestoreMessage),
            new ViewModels.WorkspaceThumbnailPort(CaptureWorkspaceUrlThumbnailAsync));
        view.DataContext = viewModel;
        view.ConfigureRecentItems(App.RecentItems, recordActions: !_isPrivateWindow);
        view.ConfigureRecentItemOpening(OpenRecentItemAsync);
        view.ConfigureRecentItemThumbnailFetching(FetchRecentItemThumbnailAsync);
        view.ConfigureExternalBrowserOpening(
            IsExternalBrowserAvailable,
            OpenUrlsInExternalBrowserAsync);
        view.ConfigureWorkspaceNoteAppend(EditWorkspaceNoteAsync);
        view.ConfigureCommandExecution(
            (actionId, parameter) => ExecuteRegisteredCommandAsync(
                actionId,
                workspaceTab,
                parameter));
        view.ConfigureGestures(
            _settings,
            (command, selectedText) => ExecuteGestureCommandForTargetAsync(
                command,
                selectedText,
                workspaceTab),
            () => TryBeginWorkspaceGesture(workspaceTab),
            CompleteWorkspaceGesture,
            () => TryBeginWorkspaceRightWheel(workspaceTab.Id));
        view.ConfigureShellFolderTabs(OpenShellFolderInTab);
        view.ConfigureLocalVideoPlayer(path => OpenLocalVideoMiniPlayer(path));
        view.TabShelfTabTransferRequested += (_, args) =>
            args.Accepted = TryTransferTabShelfItem(args.Request);
        view.QuickLookPaneLayoutChanged += QuickLookPaneLayoutChanged;
        view.QuickLookTextDisplayChanged += QuickLookTextDisplayChanged;
        view.ConfigureQuickLook(_settings.QuickLook);
        return view;
    }

    private void QueueWorkspaceTabWarmup(BrowserTab workspaceTab)
    {
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isClosingWindow || !_tabs.Contains(workspaceTab))
            {
                return;
            }

            EnsureWorkspaceViewHosted(workspaceTab);
            if (!ReferenceEquals(ActiveTab, workspaceTab))
            {
                HideWorkspaceTab();
            }
        }), DispatcherPriority.ContextIdle);
    }

    private Views.WorkspaceOSView? EnsureWorkspaceViewHosted(BrowserTab tab)
    {
        if (GetTabHostedContent(tab) is null)
        {
            var createdView = CreateWorkspaceOSView(tab);
            try
            {
                s_tabRuntimeCoordinator.RegisterHostedContent(tab, createdView);
            }
            catch (Exception operationException)
            {
                Exception? cleanupException = null;
                try
                {
                    if (createdView is IDisposable disposableCreatedView)
                    {
                        disposableCreatedView.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    cleanupException = ex;
                }

                if (cleanupException is not null)
                {
                    throw new AggregateException(
                        "Workspace Viewの登録と後始末に失敗しました。",
                        operationException,
                        cleanupException);
                }

                throw;
            }
        }

        if (GetTabHostedContent(tab) is not Views.WorkspaceOSView workspaceView)
        {
            return null;
        }

        if (!ReferenceEquals(WorkspaceHost.Content, workspaceView))
        {
            DetachHostedContent(workspaceView);
            WorkspaceHost.Content = workspaceView;
        }

        return workspaceView;
    }

    private void ShowWorkspaceTab(BrowserTab tab)
    {
        HideSplitViewForSingleTab(null);
        HideAllFolderTabContents();
        if (_displayedTab is not null)
        {
            MoveWebViewToBackground(_displayedTab);
            _displayedTab = null;
        }

        PlaceholderHost.Visibility = Visibility.Collapsed;
        if (EnsureWorkspaceViewHosted(tab) is { } workspaceView)
        {
            WorkspaceHost.Visibility = Visibility.Visible;
            workspaceView.SetActive(true);
            if (_isInteractiveWindowMoveResize)
            {
                workspaceView.BeginInteractiveResizePreview();
            }
            workspaceView.FocusShortcutScope();
        }

        SetAddressText(string.Empty);
    }

    private void ShowWorkspaceOSWindow()
    {
        if (_workspaceOSWindow is { IsVisible: true })
        {
            _workspaceOSWindow.Activate();
            return;
        }

        var inputScopeId = Guid.NewGuid();
        var window = new WorkspaceOSWindow(
            new WorkspaceService(),
            _settings.LastWorkspaceId,
            _settings,
            OpenWorkspaceUrlAsync,
            OpenWorkspaceSessionTabsAsync,
            OpenWorkspaceSessionTabsInNewWindowAsync,
            CaptureWorkspaceMediaOverlayShelfItems,
            CaptureWorkspaceMediaOverlaysAsync,
            OpenWorkspaceMediaSetAsync,
            CaptureWorkspaceUrlThumbnailAsync,
            SaveLastWorkspaceId,
            CaptureWorkspaceOpenTabs,
            CaptureWorkspaceCurrentTab,
            CloseWorkspaceTabAsync,
            inputScopeId,
            () => TryBeginWorkspaceGesture(inputScopeId),
            () => TryBeginWorkspaceRightWheel(inputScopeId),
            CompleteWorkspaceGesture,
            ExecuteGestureCommandAsync,
            OpenWorkspaceFolderWindow,
            OpenShellFolderInTab,
            path => OpenLocalVideoMiniPlayer(path),
            QuickLookPaneLayoutChanged,
            QuickLookTextDisplayChanged,
            () => _workspaceSessionController.DeferredRestoreMessage)
        {
            Owner = this
        };
        window.ConfigureRecentItems(App.RecentItems, recordActions: !_isPrivateWindow);
        window.ConfigureRecentItemOpening(OpenRecentItemAsync);
        window.ConfigureRecentItemThumbnailFetching(FetchRecentItemThumbnailAsync);
        window.ConfigureExternalBrowserOpening(
            IsExternalBrowserAvailable,
            OpenUrlsInExternalBrowserAsync);
        window.ConfigureWorkspaceNoteAppend(EditWorkspaceNoteAsync);
        window.Closed += (_, _) =>
        {
            CancelWorkspaceInputScope(inputScopeId);
            if (ReferenceEquals(_workspaceOSWindow, window))
            {
                _workspaceOSWindow = null;
            }
        };

        _workspaceOSWindow = window;
        window.Show();
    }

    private void OpenWorkspaceFolderWindow(WorkspaceFolderItem folder, Guid? selectedItemId)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(() => OpenWorkspaceFolderWindow(folder, selectedItemId));
            return;
        }

        if (_workspaceFolderWindows.TryGetValue(folder.Id, out var existingWindow) && existingWindow.IsVisible)
        {
            _ = existingWindow.ShowItemAsync(selectedItemId);
            existingWindow.Activate();
            return;
        }

        var inputScopeId = Guid.NewGuid();
        _settings.WorkspaceFolderWindowPlacements ??= [];
        _settings.WorkspaceFolderWindowPlacements.TryGetValue(folder.Id, out var initialPlacement);
        var window = new WorkspaceFolderWindow(
            new WorkspaceService(),
            folder.WorkspaceId,
            folder,
            selectedItemId,
            _settings,
            initialPlacement,
            OpenWorkspaceUrlAsync,
            OpenWorkspaceSessionTabsAsync,
            OpenWorkspaceSessionTabsInNewWindowAsync,
            CaptureWorkspaceMediaOverlayShelfItems,
            CaptureWorkspaceMediaOverlaysAsync,
            OpenWorkspaceMediaSetAsync,
            CaptureWorkspaceUrlThumbnailAsync,
            SaveLastWorkspaceId,
            CaptureWorkspaceOpenTabs,
            CaptureWorkspaceCurrentTab,
            CloseWorkspaceTabAsync,
            inputScopeId,
            () => TryBeginWorkspaceGesture(inputScopeId),
            () => TryBeginWorkspaceRightWheel(inputScopeId),
            CompleteWorkspaceGesture,
            ExecuteGestureCommandAsync,
            OpenWorkspaceFolderWindow,
            OpenShellFolderInTab,
            path => OpenLocalVideoMiniPlayer(path),
            QuickLookPaneLayoutChanged,
            QuickLookTextDisplayChanged,
            () => _workspaceSessionController.DeferredRestoreMessage)
        {
            Owner = this
        };
        window.ConfigureRecentItems(App.RecentItems, recordActions: !_isPrivateWindow);
        window.ConfigureExternalBrowserOpening(
            IsExternalBrowserAvailable,
            OpenUrlsInExternalBrowserAsync);
        window.ConfigureWorkspaceNoteAppend(EditWorkspaceNoteAsync);
        window.Closed += (_, _) =>
        {
            if (window.CapturedWindowPlacement is { } placement)
            {
                SaveWorkspaceFolderWindowPlacement(folder.Id, placement);
            }

            CancelWorkspaceInputScope(inputScopeId);
            if (_workspaceFolderWindows.TryGetValue(folder.Id, out var registeredWindow)
                && ReferenceEquals(registeredWindow, window))
            {
                _workspaceFolderWindows.Remove(folder.Id);
            }
        };

        _workspaceFolderWindows[folder.Id] = window;
        window.Show();
    }

    private void RefreshWorkspaceGestureSettings()
    {
        _blankViewGestureController.UpdateSettings(_settings);

        foreach (var tab in _tabs)
        {
            if (GetTabHostedContent(tab) is Views.WorkspaceOSView workspaceView)
            {
                workspaceView.UpdateGestureSettings(_settings);
                workspaceView.UpdateQuickLookSettings(_settings.QuickLook);
            }
            else if (GetTabHostedContent(tab) is Views.FolderTabView folderTabView)
            {
                folderTabView.UpdateGestureSettings(_settings);
                folderTabView.UpdateQuickLookSettings(_settings.QuickLook);
            }
        }

        _workspaceOSWindow?.UpdateGestureSettings(_settings);
        _workspaceOSWindow?.UpdateQuickLookSettings(_settings.QuickLook);
        foreach (var window in _workspaceFolderWindows.Values.ToList())
        {
            window.UpdateGestureSettings(_settings);
            window.UpdateQuickLookSettings(_settings.QuickLook);
        }
    }

    private async Task RefreshGestureScriptOnLoadedTabsAsync()
    {
        if (string.IsNullOrWhiteSpace(_gestureScript))
        {
            return;
        }

        foreach (var tab in _tabs.ToList())
        {
            if (GetTabWebView(tab)?.CoreWebView2 is not { } core)
            {
                continue;
            }

            try
            {
                var script = BuildGestureScript(tab);
                await CoreWebViewDocumentScriptRegistry.UpdateVariableAsync(
                    core,
                    "gesture-host",
                    script);
                await core.ExecuteScriptAsync(script);
            }
            catch
            {
                // ページ状態によって実行できない場合は、次回ナビゲーション時の反映に任せます。
            }
        }
    }

    private async Task OpenWorkspaceUrlAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            var task = await Dispatcher.InvokeAsync(() => OpenWorkspaceUrlAsync(url));
            await task;
            return;
        }

        var target = _addressService.Resolve(url);
        var activeTab = ActiveTab;
        if (activeTab is not null && IsNewTabUrl(activeTab.Url))
        {
            await NavigateTabToUrlAsync(activeTab, target);
        }
        else
        {
            OpenTab(target, activate: true);
        }
    }

    private Task<int> OpenWorkspaceSessionTabsAsync(IReadOnlyList<WorkspaceSessionTab> tabs)
    {
        if (!Dispatcher.CheckAccess())
        {
            return Dispatcher.InvokeAsync(() => OpenWorkspaceSessionTabsAsync(tabs)).Task.Unwrap();
        }

        return Task.FromResult(_workspaceSessionController.OpenTabs(tabs));
    }

    private Task OpenWorkspaceSessionTabsInNewWindowAsync(IReadOnlyList<WorkspaceSessionTab> tabs)
    {
        if (!Dispatcher.CheckAccess())
        {
            return Dispatcher.InvokeAsync(() => OpenWorkspaceSessionTabsInNewWindowAsync(tabs)).Task.Unwrap();
        }

        _workspaceSessionController.OpenInNewWindow(tabs, initialTabPairs =>
        {
            var position = new Point(Math.Max(0, Left + 32), Math.Max(0, Top + 32));
            var initialTabs = initialTabPairs.Select(static pair => pair.First).ToList();
            var window = new MainWindow(
                initialTabs,
                persistSession: false,
                position,
                isPrivateWindow: _isPrivateWindow,
                restoreWorkspaceSession: true);
            window._workspaceSessionController.CompleteNewWindowRestore(initialTabPairs);
            window.Show();
            window.Activate();
            if (window._workspaceSessionController.DeferredRestoreMessage is { } message)
            {
                SetStatus(message, autoClear: false);
            }
        });
        return Task.CompletedTask;
    }

    IList<BrowserTab> IWorkspaceSessionHost.Tabs => _tabs;
    Guid IWorkspaceSessionHost.WindowId => _workspaceWindowId;
    string IWorkspaceSessionHost.ResolveUrl(string url) => _addressService.Resolve(url);
    bool IWorkspaceSessionHost.HasContainerProfile(string containerProfileId)
        => _containerProfiles.Any(profile => string.Equals(profile.Id, containerProfileId, StringComparison.OrdinalIgnoreCase));
    BrowserTab IWorkspaceSessionHost.CreateFolderTab(string url, string title, string containerProfileId)
    {
        var tab = CreateFolderTab(url, title: title,
            persistentState: BrowserTabStateMapper.FromSessionTabState(new SessionTabState
            {
                Url = url,
                Title = title,
                IsFolderTab = true,
                ContainerProfileId = containerProfileId
            }));
        ApplyContainerProfileToTab(tab);
        return tab;
    }
    BrowserTab IWorkspaceSessionHost.OpenTab(string url, string title, string containerProfileId)
        => OpenTab(url, activate: false, title, queueBackgroundLoading: false, containerProfileId: containerProfileId);
    void IWorkspaceSessionHost.SetStatus(string message, bool autoClear) => SetStatus(message, autoClear);
    void IWorkspaceSessionHost.SelectTab(BrowserTab tab) => SelectTab(tab);
    void IWorkspaceSessionHost.QueueSelectedTabActivation() => QueueSelectedTabActivation();
    void IWorkspaceSessionHost.QueueBackgroundTabLoading() => QueueBackgroundTabLoading();
    void IWorkspaceSessionHost.PersistSplitViewState() => PersistSplitViewState();
    void IWorkspaceSessionHost.RefreshVerticalTabViews() => RefreshVerticalTabViews();
    void IWorkspaceSessionHost.QueueSessionSave() => QueueSessionSave();
    void IWorkspaceSessionHost.QueueWorkspaceOpenTabsRefresh() => QueueWorkspaceOpenTabsRefresh();

    private IReadOnlyList<CapturedTabSnapshot> CaptureWorkspaceOpenTabs()
    {
        var result = new List<CapturedTabSnapshot>();
        foreach (var window in Application.Current.Windows
            .OfType<MainWindow>()
            .Where(static x => x.IsVisible)
            .OrderBy(static x => x._workspaceWindowIndex))
        {
            result.AddRange(_tabCaptureService.CaptureTabs(
                window._tabs.ToList(),
                window.GetActiveBrowserTabIdForWorkspace(),
                window._workspaceWindowId,
                GetWorkspaceWindowGroupTitle(window),
                window._workspaceWindowIndex,
                !ReferenceEquals(window, this),
                window._splitViewService.Snapshot()));
        }

        return result;
    }

    private CapturedTabSnapshot? CaptureWorkspaceCurrentTab()
    {
        return _tabCaptureService.CaptureActiveTab(
            _tabs.ToList(),
            GetActiveBrowserTabIdForWorkspace(),
            _workspaceWindowId,
            GetWorkspaceWindowGroupTitle(this),
            _workspaceWindowIndex,
            isForeignWindow: false,
            _splitViewService.Snapshot());
    }

    private string GetWorkspaceWindowGroupTitle(MainWindow window)
    {
        return ReferenceEquals(window, this)
            ? "このウィンドウ"
            : $"別ウィンドウ {window._workspaceWindowIndex}";
    }

    private Guid? GetActiveBrowserTabIdForWorkspace()
        => TabCaptureService.ResolveActiveBrowserTabId(_tabs, ActiveTab, _lastActiveBrowserTabId);

    private async Task CloseWorkspaceTabAsync(Guid tabId)
    {
        if (!Dispatcher.CheckAccess())
        {
            var task = await Dispatcher.InvokeAsync(() => CloseWorkspaceTabAsync(tabId));
            await task;
            return;
        }

        foreach (var window in Application.Current.Windows.OfType<MainWindow>().ToList())
        {
            var tab = window._tabs.FirstOrDefault(x => x.Id == tabId);
            if (tab is null)
            {
                continue;
            }

            window.CloseTab(tab);
            return;
        }

        TryCloseWorkspaceMediaOverlay(tabId);
    }

    private void SaveLastWorkspaceId(Guid workspaceId)
    {
        if (_settings.LastWorkspaceId == workspaceId)
        {
            return;
        }

        _settings.LastWorkspaceId = workspaceId;
        SettingsService.Save(_settings);
    }

    private void SaveWorkspaceFolderWindowPlacement(
        Guid folderId,
        WorkspaceFolderWindowPlacementSettings placement)
    {
        if (WorkspaceFolderWindowPlacementProfile.Update(_settings, folderId, placement))
        {
            SettingsService.Save(_settings);
        }
    }

    private void QueueBookmarkOpen(BookmarkItem bookmark)
    {
        _windowTaskSupervisor.TryRun(
            "bookmark-open",
            cancellationToken => OpenBookmarkAsync(bookmark, cancellationToken));
    }

    private async Task OpenBookmarkAsync(
        BookmarkItem bookmark,
        CancellationToken cancellationToken = default)
    {
        await _bookmarkController.OpenAsync(bookmark, cancellationToken);
    }

    private void QueueBookmarkDelete(Guid bookmarkId)
    {
        _windowTaskSupervisor.TryRun(
            "bookmark-delete",
            cancellationToken => DeleteBookmarkFromNewTabAsync(bookmarkId, cancellationToken));
    }

    private async Task DeleteBookmarkFromNewTabAsync(
        Guid bookmarkId,
        CancellationToken cancellationToken)
    {
        await _bookmarkController.DeleteAsync(bookmarkId, cancellationToken);
    }

    BrowserTab? IBookmarkControllerHost.GetActiveBrowserCommandTarget() => GetActiveBrowserCommandTarget();

    string? IBookmarkControllerHost.GetCurrentPageUrl(BrowserTab tab) => GetCurrentPageUrl(tab);

    string IBookmarkControllerHost.GetCurrentPageTitle(BrowserTab tab, string url)
        => GetCurrentPageTitle(tab, url);

    BrowserTab? IBookmarkControllerHost.ActiveTab => ActiveTab;

    bool IBookmarkControllerHost.IsNewTab(BrowserTab tab) => IsNewTabUrl(tab.Url);

    Task IBookmarkControllerHost.NavigateTabToUrlAsync(
        BrowserTab tab,
        string url,
        string title,
        CancellationToken cancellationToken)
        => NavigateTabToUrlAsync(tab, url, title, cancellationToken: cancellationToken);

    void IBookmarkControllerHost.OpenTab(string url, string title)
        => OpenTab(url, activate: true, title);

    void IBookmarkControllerHost.CloseAddressSuggestions() => CloseAddressSuggestions();

    void IBookmarkControllerHost.RefreshLoadedNewTabPages() => RefreshLoadedNewTabPages();

    void IBookmarkControllerHost.QueueSessionSave() => QueueSessionSave();

    void IBookmarkControllerHost.SetStatus(string message) => SetStatus(message, autoClear: true);

    void IBookmarkControllerHost.SetPersistentStatus(string message) => SetStatus(message);

    private void QueueNewTabSearchNavigation(BrowserTab tab, string url)
    {
        _windowTaskSupervisor.TryRun(
            "new-tab-search-navigation",
            cancellationToken => NavigateTabToUrlAsync(tab, url, cancellationToken: cancellationToken));
    }

    private async Task NavigateTabToUrlAsync(
        BrowserTab tab,
        string url,
        string? title = null,
        bool updateAddressText = true,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        url = NormalizeYouTubeShortsUrl(url);
        if (tab.IsNativeContentTab)
        {
            OpenTab(url, activate: true, title);
            return;
        }

        tab.Url = url;
        tab.Title = title;
        ResetWebViewLoadFailureForRetry(tab);
        await EnsureTabLoadedAsync(tab, null);
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        NavigateCore(tab, url);
        if (updateAddressText)
        {
            SetAddressText(url);
        }

        QueueSessionSave();
    }

    private void RefreshLoadedNewTabPages()
    {
        foreach (var tab in _tabs.Where(IsLoadedNewTabPage))
        {
            tab.Url = NewTabUrl;
            tab.Title = "新しいタブ";
            NavigateCore(tab, NewTabUrl);
        }
    }

    private static bool IsLoadedNewTabPage(BrowserTab tab)
    {
        if (GetTabWebView(tab)?.CoreWebView2 is not { } core)
        {
            return false;
        }

        return IsNewTabUrl(tab.Url)
            || IsGeneratedNewTabSource(tab.Url) && string.Equals(tab.Title, "新しいタブ", StringComparison.Ordinal)
            || IsGeneratedNewTabSource(core.Source) && string.Equals(tab.Title, "新しいタブ", StringComparison.Ordinal);
    }

    private static string? GetCurrentPageUrl(BrowserTab tab)
    {
        if (tab.IsNativeContentTab)
        {
            return null;
        }

        if (IsUserGuideUrl(tab.Url))
        {
            return UserGuideUrl;
        }

        if (IsNewTabUrl(tab.Url))
        {
            return null;
        }

        var source = TryGetCoreWebView2Source(tab);
        if (!string.IsNullOrWhiteSpace(source) && !IsBlankUrl(source))
        {
            return source;
        }

        return tab.Url;
    }

    private static CoreWebView2? TryGetCoreWebView2(BrowserTab tab)
    {
        try
        {
            return GetTabWebView(tab)?.CoreWebView2;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static string? TryGetCoreWebView2Source(BrowserTab tab)
    {
        try
        {
            return GetTabWebView(tab)?.CoreWebView2?.Source;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static string GetCurrentPageTitle(BrowserTab tab, string url)
    {
        var title = GetTabWebView(tab)?.CoreWebView2?.DocumentTitle;
        if (string.IsNullOrWhiteSpace(title))
        {
            title = tab.Title;
        }

        if (!string.IsNullOrWhiteSpace(title) && !IsNewTabUrl(url))
        {
            return title.Trim();
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.Host
            : url;
    }

    private static WebView2? GetTabWebView(BrowserTab? tab)
    {
        return tab is null ? null : s_tabRuntimeCoordinator.GetWebView(tab);
    }

    private static string? GetTabWebMessageToken(BrowserTab tab)
    {
        return s_tabRuntimeCoordinator.GetWebMessageToken(tab);
    }

    private static UIElement? GetTabHostedContent(BrowserTab? tab)
    {
        return tab is null ? null : s_tabRuntimeCoordinator.GetHostedContent(tab);
    }

    private static bool IsHttpOrHttpsUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }

    private bool ShouldOpenNewWindowRequestInForegroundTab(BrowserTab sourceTab, string? targetUrl)
    {
        if (IsAuthenticationPopupUrl(targetUrl))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(targetUrl) && !IsBlankUrl(targetUrl))
        {
            return false;
        }

        return IsXOrTwitterUrl(GetCurrentPageUrl(sourceTab));
    }

    private static bool IsAuthenticationPopupUrl(string? url)
    {
        return BrowserUrlRulesService.IsAuthenticationPopupUrl(url);
    }

    private static bool IsXOrTwitterUrl(string? url)
    {
        return BrowserUrlRulesService.IsXOrTwitterUrl(url);
    }

    private static bool IsXOrTwitterHost(string host)
    {
        return BrowserUrlRulesService.IsXOrTwitterHost(host);
    }

    private static bool IsYouTubeUrl(string url)
    {
        return YouTubeUrlService.IsYouTubeUrl(url);
    }

    private static bool IsYouTubeVideoUrl(string url)
    {
        return YouTubeUrlService.IsVideoUrl(url);
    }

    private static bool TryGetYouTubeVideoId(string? url, out string videoId)
    {
        return YouTubeUrlService.TryGetVideoId(url, out videoId);
    }

    private static string NormalizeYouTubeShortsUrl(string url)
    {
        return YouTubeUrlService.NormalizeShortsUrl(url);
    }

    private void QueueYouTubeShortsHistoryReplaceNavigation(
        BrowserTab tab,
        CoreWebView2 core,
        string normalizedUrl)
    {
        _windowTaskSupervisor.TryRun(
            "youtube-shorts-history-replace-navigation",
            cancellationToken => ReplaceYouTubeShortsNavigationWithoutNewHistoryAsync(
                tab,
                core,
                normalizedUrl,
                cancellationToken));
    }

    private async Task ReplaceYouTubeShortsNavigationWithoutNewHistoryAsync(
        BrowserTab tab,
        CoreWebView2 core,
        string normalizedUrl,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await core.ExecuteScriptAsync(BuildReplaceCurrentLocationScript(normalizedUrl));
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            if (cancellationToken.IsCancellationRequested || !_tabs.Contains(tab))
            {
                return;
            }

            RefreshTabFromCore(tab);
            UpdateToolbarState();
        }
    }

    private static string BuildReplaceCurrentLocationScript(string url)
    {
        return "window.location.replace(" + JsonSerializer.Serialize(url) + ");";
    }

    private static string RemoveYouTubeNoAutoplayQuery(string url)
    {
        return YouTubeUrlService.RemoveNoAutoplayQuery(url);
    }

    private static bool ShouldDeferBackgroundMediaLoad(BrowserTab tab)
    {
        return BrowserUrlRulesService.ShouldDeferBackgroundMediaLoad(tab.Url);
    }

    private static bool IsBackgroundMediaPlaybackUrl(string url)
    {
        return BrowserUrlRulesService.IsBackgroundMediaPlaybackUrl(url);
    }

    private static bool HostMatches(string host, string domain)
    {
        return BrowserUrlRulesService.HostMatches(host, domain);
    }

    private static bool TryReadScriptString(string resultJson, string propertyName, out string value)
    {
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(propertyName, out var property)
                && property.ValueKind == JsonValueKind.String)
            {
                value = property.GetString() ?? string.Empty;
                return true;
            }
        }
        catch
        {
        }

        value = string.Empty;
        return false;
    }

    private static bool TryReadScriptDouble(string resultJson, string propertyName, out double value)
    {
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(propertyName, out var property)
                && property.TryGetDouble(out value))
            {
                return true;
            }
        }
        catch
        {
        }

        value = 0;
        return false;
    }

    private static bool TryReadScriptBoolean(string resultJson, string propertyName, out bool value)
    {
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(propertyName, out var property)
                && (property.ValueKind == JsonValueKind.True || property.ValueKind == JsonValueKind.False))
            {
                value = property.GetBoolean();
                return true;
            }
        }
        catch
        {
        }

        value = false;
        return false;
    }

    private static string? TryGetHost(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.Host
            : null;
    }

    private void OpenExtensionPage(string url)
    {
        OpenTab(url, activate: true, title: "拡張機能設定");
    }

    private void QueueExtensionToolbarRefresh(CoreWebView2Profile profile)
    {
        _windowTaskSupervisor.TryRun(
            "extension-toolbar-refresh",
            cancellationToken => RefreshExtensionToolbarAsync(profile, cancellationToken));
    }

    private async Task RefreshExtensionToolbarAsync(
        CoreWebView2Profile? profile = null,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (!_settings.EnableBrowserExtensions)
        {
            _extensionToolbarItems.Clear();
            return;
        }

        profile ??= GetExtensionProfile();
        if (profile is null)
        {
            return;
        }

        try
        {
            var manifests = ExtensionManifestInfo.LoadKnownManifests();
            var extensions = await profile.GetBrowserExtensionsAsync();
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var items = extensions
                .Where(static extension => ExtensionToolbarPolicy.ShouldShowInToolbar(extension.Name))
                .OrderBy(static x => x.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(extension =>
                {
                    var manifest = ExtensionManifestInfo.FindByName(manifests, extension.Name);
                    return new ExtensionToolbarItem(extension, manifest, LoadExtensionIcon(manifest?.IconPath));
                })
                .ToList();

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _extensionToolbarItems.Clear();
            foreach (var item in items)
            {
                _extensionToolbarItems.Add(item);
            }
        }
        catch (Exception ex)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            SetStatus("拡張機能アイコンを更新できません: " + ex.Message, autoClear: true);
        }
    }

    private CoreWebView2Profile? GetExtensionProfile()
    {
        foreach (var tab in _tabs)
        {
            if (GetTabWebView(tab)?.CoreWebView2?.Profile is { } profile)
            {
                return profile;
            }
        }

        return null;
    }

    private async void ExtensionIconButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ExtensionToolbarItem item
            || sender is not Button button)
        {
            return;
        }

        await ShowExtensionPopupAsync(item, button);
    }

    private void ExtensionIconButton_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ExtensionToolbarItem item
            || sender is not Button button)
        {
            return;
        }

        e.Handled = true;
        ShowExtensionIconMenu(item, button);
    }

    private void ShowExtensionIconMenu(ExtensionToolbarItem item, Button button)
    {
        CloseExtensionPopup();

        var menu = CreateExtensionIconMenu(item);
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private ContextMenu CreateExtensionIconMenu(ExtensionToolbarItem item)
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem
        {
            Header = item.Name,
            IsEnabled = false
        });
        menu.Items.Add(new Separator());

        var settingsUrl = item.GetSettingsUrl();
        var settingsItem = new MenuItem
        {
            Header = "設定を開く",
            IsEnabled = !string.IsNullOrWhiteSpace(settingsUrl)
        };
        settingsItem.Click += (_, _) => OpenExtensionToolbarPage(settingsUrl, "拡張機能設定", "設定ページが見つかりません");
        menu.Items.Add(settingsItem);

        var toggleItem = new MenuItem
        {
            Header = item.IsExtensionEnabled ? "無効にする" : "有効にする"
        };
        toggleItem.Click += async (_, _) => await ToggleExtensionFromToolbarAsync(item);
        menu.Items.Add(toggleItem);

        menu.Items.Add(new Separator());

        var manageItem = new MenuItem
        {
            Header = "拡張機能を管理"
        };
        manageItem.Click += async (_, _) => await OpenExtensionManagerAsync();
        menu.Items.Add(manageItem);

        var removeItem = new MenuItem
        {
            Header = "削除"
        };
        removeItem.Click += async (_, _) => await RemoveExtensionFromToolbarAsync(item);
        menu.Items.Add(removeItem);

        return menu;
    }

    private void OpenExtensionToolbarPage(string? url, string title, string missingMessage)
    {
        CloseExtensionPopup();

        if (string.IsNullOrWhiteSpace(url))
        {
            SetStatus(missingMessage, autoClear: true);
            return;
        }

        OpenTab(url, activate: true, title: title);
    }

    private async Task ShowExtensionPopupAsync(ExtensionToolbarItem item, Button button)
    {
        if (_isClosingWindow)
        {
            return;
        }

        if (!item.IsExtensionEnabled)
        {
            SetStatus("無効な拡張機能です", autoClear: true);
            return;
        }

        var popupUrl = item.GetPopupUrl();
        if (string.IsNullOrWhiteSpace(popupUrl))
        {
            SetStatus("ポップアップページが見つかりません", autoClear: true);
            return;
        }

        if (_extensionPopup?.IsOpen == true
            && _extensionPopupPlacementTarget == button
            && _extensionPopupRequestCoordinator.IsCurrentUrl(popupUrl))
        {
            CloseExtensionPopup();
            return;
        }

        try
        {
            var requestVersion = _extensionPopupRequestCoordinator.BeginRequest();
            var popup = EnsureExtensionPopup();
            _extensionPopupPlacementTarget = button;
            _extensionPopupRequestCoordinator.SetCurrentUrl(popupUrl);

            popup.PlacementTarget = button;
            popup.Placement = PlacementMode.Bottom;
            popup.HorizontalOffset = 0;
            popup.VerticalOffset = 4;
            popup.IsOpen = true;

            if (_extensionPopupWebView is null)
            {
                throw new InvalidOperationException("ポップアップ WebView2 を作成できませんでした");
            }

            var webView = _extensionPopupWebView;
            await webView.EnsureCoreWebView2Async(await EnsureWebViewEnvironmentAsync());
            if (_isClosingWindow
                || !_extensionPopupRequestCoordinator.IsCurrent(requestVersion)
                || !ReferenceEquals(_extensionPopupWebView, webView)
                || !ReferenceEquals(_extensionPopup, popup)
                || popup.IsOpen != true
                || !_extensionPopupRequestCoordinator.IsCurrentUrl(popupUrl))
            {
                return;
            }

            ConfigureExtensionPopupWebView(webView.CoreWebView2);
            webView.CoreWebView2.Navigate(popupUrl);
            webView.Focus();
            SetStatus($"{item.Name} のポップアップを表示しました", autoClear: true);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            CloseExtensionPopup();
            SetStatus("WebView2 Runtime が見つかりません", autoClear: true);
        }
        catch (Exception ex)
        {
            CloseExtensionPopup();
            SetStatus("拡張機能ポップアップを表示できません: " + ex.Message, autoClear: true);
        }
    }

    private Popup EnsureExtensionPopup()
    {
        if (_extensionPopup is not null)
        {
            return _extensionPopup;
        }

        _extensionPopupWebView = new WebView2
        {
            Width = ExtensionPopupWidth,
            Height = ExtensionPopupHeight
        };

        var frame = new Border
        {
            Width = ExtensionPopupWidth,
            Height = ExtensionPopupHeight,
            Background = new SolidColorBrush(Color.FromRgb(17, 20, 26)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(58, 64, 77)),
            BorderThickness = new Thickness(1),
            Child = _extensionPopupWebView
        };

        _extensionPopup = new Popup
        {
            AllowsTransparency = false,
            PopupAnimation = PopupAnimation.Fade,
            StaysOpen = false,
            Child = frame
        };
        _extensionPopup.Closed += (_, _) =>
        {
            _extensionPopupRequestCoordinator.ClearCurrentUrl();
            _extensionPopupPlacementTarget = null;
        };

        return _extensionPopup;
    }

    private static void ConfigureExtensionPopupWebView(CoreWebView2 core)
    {
        WebViewLifecycleService.ApplyDefaultCoreSettings(core);
    }

    private void CloseExtensionPopup(bool disposeWebView = false)
    {
        _extensionPopupRequestCoordinator.Invalidate();

        if (_extensionPopup is not null)
        {
            _extensionPopup.IsOpen = false;
        }

        _extensionPopupRequestCoordinator.ClearCurrentUrl();
        _extensionPopupPlacementTarget = null;

        if (!disposeWebView)
        {
            return;
        }

        _extensionPopupWebView?.Dispose();
        _extensionPopupWebView = null;
        _extensionPopup = null;
    }

    private void QueueBookmarkPreviewShow(
        string? url, string? title, double? left, double? top, double? right, double? bottom)
        => _bookmarkPreviewController.QueueBookmarkPreviewShow(url, title, left, top, right, bottom);

    private void CloseBookmarkPreview(bool clearNavigation = false, bool disposeWebView = false)
        => _bookmarkPreviewController.CloseBookmarkPreview(clearNavigation, disposeWebView);

    bool IBookmarkPreviewHost.IsClosingWindow => _isClosingWindow;

    bool IBookmarkPreviewHost.HasLoadedNewTabPage => _tabs.Any(IsLoadedNewTabPage);

    string IBookmarkPreviewHost.ResolveUrl(string input) => _addressService.Resolve(input);

    Task<CoreWebView2Environment> IBookmarkPreviewHost.EnsureEnvironmentAsync()
        => EnsureWebViewEnvironmentAsync();

    void IBookmarkPreviewHost.ReportError(string message) => SetStatus(message, autoClear: true);

    private async Task ToggleExtensionFromToolbarAsync(ExtensionToolbarItem item)
    {
        try
        {
            await item.Extension.EnableAsync(!item.IsExtensionEnabled);
            item.IsExtensionEnabled = item.Extension.IsEnabled;
            SetStatus($"{item.Name} を{item.EnabledText}にしました", autoClear: true);
            await RefreshExtensionToolbarAsync();
        }
        catch (Exception ex)
        {
            SetStatus("拡張機能の状態を変更できません: " + ex.Message, autoClear: true);
        }
    }

    private async Task RemoveExtensionFromToolbarAsync(ExtensionToolbarItem item)
    {
        var result = MessageBox.Show(
            this,
            $"{item.Name} を削除しますか?",
            "拡張機能の削除",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await item.Extension.RemoveAsync();
            _extensionToolbarItems.Remove(item);
            SetStatus($"{item.Name} を削除しました", autoClear: true);
            await RefreshExtensionToolbarAsync();
        }
        catch (Exception ex)
        {
            SetStatus("拡張機能を削除できません: " + ex.Message, autoClear: true);
        }
    }

    private static ImageSource? LoadExtensionIcon(string? iconPath)
    {
        if (string.IsNullOrWhiteSpace(iconPath) || !File.Exists(iconPath))
        {
            return null;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 24;
            image.UriSource = new Uri(iconPath);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private void TrackAddedTabRuntimeOwnership(NotifyCollectionChangedEventArgs args)
    {
        if (args.NewItems is null)
        {
            return;
        }

        foreach (var tab in args.NewItems.OfType<BrowserTab>())
        {
            var snapshot = s_tabRuntimeCoordinator.GetOwnershipSnapshot(tab);
            if (snapshot.State == TabRuntimeState.Transferring
                && snapshot.TransferTargetWindowId == _workspaceWindowId)
            {
                continue;
            }

            var result = s_tabRuntimeCoordinator.Attach(tab, _workspaceWindowId);
            if (!result.Succeeded)
            {
                RecordTabRuntimeTransitionFailure(tab, "attach", result);
            }
        }
    }

    private void DetachPreservedTabRuntime(BrowserTab tab)
    {
        var result = s_tabRuntimeCoordinator.Detach(tab, _workspaceWindowId);
        if (!result.Succeeded)
        {
            RecordTabRuntimeTransitionFailure(tab, "detach-preserved", result);
        }
    }

    private void VerifyRemovedTransferredTabRuntime(BrowserTab tab)
    {
        var snapshot = s_tabRuntimeCoordinator.GetOwnershipSnapshot(tab);
        if (snapshot.State == TabRuntimeState.Transferring
            && snapshot.TransferSourceWindowId == _workspaceWindowId)
        {
            return;
        }

        RecordTabRuntimeTransitionFailure(
            tab,
            "remove-transfer-source",
            new TabRuntimeTransitionResult(
                TabRuntimeTransitionStatus.Rejected,
                snapshot,
                "source-removal-without-transfer"));
    }

    private void RecordTabRuntimeTransitionFailure(
        BrowserTab tab,
        string operation,
        TabRuntimeTransitionResult result)
    {
        var snapshot = result.Snapshot;
        AppendDiagnosticLogLine(
            "runner=tab-runtime",
            $"operation={NormalizeSessionDiagnosticToken(operation)}",
            $"window={_workspaceWindowId:N}",
            $"tab={tab.Id:N}",
            $"runtime={snapshot.RuntimeId:N}",
            $"state={snapshot.State}",
            $"owner={snapshot.OwnerWindowId?.ToString("N") ?? "none"}",
            $"source={snapshot.TransferSourceWindowId?.ToString("N") ?? "none"}",
            $"target={snapshot.TransferTargetWindowId?.ToString("N") ?? "none"}",
            $"generation={snapshot.TransferGeneration}",
            $"reason={NormalizeSessionDiagnosticToken(result.FailureReason ?? "unknown")}");
    }

    private void CloseTab(BrowserTab tab)
    {
        CloseTabContextMenuForTab(tab);
        CancelInputInteractionForTab(tab);
        ClearAmazonPrimeVideoState(tab);
        ResetYouTubePlaybackDiagnostic("tab", tab.Id);
        CancelInactiveYouTubeTabCheck(tab);
        CancelSitePermissionPromptsForTab(tab.Id);
        ClosePageFindForTab(tab);
        CancelPageZoomNavigation(tab);
        _closedTabLifecycleController.ClosePreparedTab(tab);
    }

    private static ClosedTabState CreateClosedTabState(
        BrowserTab tab,
        DateTimeOffset closedAt,
        bool isMuted)
    {
        var closedUrl = tab.IsFolderTab
            ? tab.Url?.Trim()
            : NormalizeSessionTabUrl(tab.Url, tab.Title);
        var navigationHistory = new List<string>();
        var navigationHistoryIndex = -1;
        if (!tab.IsFolderTab)
        {
            navigationHistory = CreatePersistedNavigationHistory(
                tab,
                closedUrl,
                out navigationHistoryIndex);
        }

        return new ClosedTabState
        {
            SourceTabId = tab.Id,
            Url = closedUrl,
            Title = tab.Title,
            IsFolderTab = tab.IsFolderTab,
            FolderTabViewMode = (GetTabHostedContent(tab) as Views.FolderTabView)?.ViewMode,
            FolderTabSortMode = (GetTabHostedContent(tab) as Views.FolderTabView)?.SortMode,
            FolderTabSortDirection = (GetTabHostedContent(tab) as Views.FolderTabView)?.SortDirection,
            FolderTabState = (GetTabHostedContent(tab) as Views.FolderTabView)?.CaptureSessionState(),
            RuntimeThumbnail = CreateClosedTabRuntimeThumbnail(tab),
            ClosedAt = closedAt,
            NavigationHistory = navigationHistory,
            NavigationHistoryIndex = navigationHistoryIndex,
            AudioPan = tab.AudioPan,
            IsMuted = isMuted,
            AudioBoostDecibels = tab.AudioBoostDecibels,
            ContainerProfileId = tab.ContainerProfileId,
            YouTubePlaybackVideoId = tab.YouTubePlaybackVideoId,
            YouTubePlaybackPositionSeconds = tab.YouTubePlaybackPositionSeconds,
            YouTubePlaybackSpeed = tab.YouTubePlaybackSpeed,
            YouTubePlaybackIsPaused = tab.YouTubePlaybackIsPaused,
            YouTubePlaybackIsLooping = tab.YouTubePlaybackIsLooping,
            YouTubePlaybackIsTheaterMode = tab.YouTubePlaybackIsTheaterMode
        };
    }

    private void RestoreRecentlyClosedItem()
        => _recentClosedItemsController.RestoreMostRecentItem();

    private void RestoreClosedTab(ClosedTabState? closedTab)
    {
        if (closedTab?.Url is null)
        {
            SetStatus("閉じたタブ履歴はありません", autoClear: true);
            return;
        }

        var restoreWindow = GetClosedItemRestoreWindow(closedTab.SourceWindowId);
        if (!restoreWindow.RestoreClosedTabInCurrentWindow(closedTab))
        {
            return;
        }

        if (!ReferenceEquals(restoreWindow, this))
        {
            if (restoreWindow.WindowState == WindowState.Minimized)
            {
                restoreWindow.WindowState = WindowState.Normal;
            }

            restoreWindow.Activate();
        }
    }

    private bool RestoreClosedTabInCurrentWindow(ClosedTabState closedTab)
        => _closedTabLifecycleController.RestoreClosedTabInCurrentWindow(closedTab);

    private static bool CanPreserveClosedTabRuntime(BrowserTab tab)
    {
        return s_tabRuntimeCoordinator.CanPreserveRuntime(tab);
    }

    private void QueueRestoredTabBridgeCompatibilityProbe(BrowserTab tab)
    {
        _windowTaskSupervisor.TryRun(
            "closed-tab-runtime-bridge-compatibility",
            cancellationToken => RestoreWebViewBridgeCompatibilityStateAsync(tab, cancellationToken));
    }

    private void QueuePreservedClosedTabSuspension(BrowserTab tab)
    {
        _windowTaskSupervisor.TryRun(
            "closed-tab-runtime-suspend",
            _ => _closedTabLifecycleController.SuspendPreservedClosedTabAsync(tab));
    }

    private void TrimClosedTabHistory()
        => _closedTabLifecycleController.TrimClosedTabHistory();

    private static void ApplyClosedTabHistoryRetention(IEnumerable<ClosedTabState> removedTabs)
        => ClosedTabLifecycleController.ApplyHistoryRetention(
            removedTabs, s_closedTabWindowHistoryStore, GetClosedTabLifecycleControllers);

    private static IReadOnlyList<ClosedTabLifecycleController> GetClosedTabLifecycleControllers()
        => Application.Current.Windows.OfType<MainWindow>()
            .Select(static window => window._closedTabLifecycleController)
            .ToList();

    private void DisposeClosedTabRuntimes()
        => _closedTabLifecycleController.DisposePreservedRuntimes();

    IList<BrowserTab> IClosedTabLifecycleHost.Tabs => _tabs;
    BrowserTab? IClosedTabLifecycleHost.ActiveTab => ActiveTab;
    Guid IClosedTabLifecycleHost.SessionWindowId => _sessionWindowId;
    Guid IClosedTabLifecycleHost.RuntimeWindowId => _workspaceWindowId;
    bool IClosedTabLifecycleHost.PersistSession => _persistSession;
    bool IClosedTabLifecycleHost.IsApplicationFullscreenActive => _youTubeControlWorkCoordinator.IsApplicationFullscreenActive;
    bool IClosedTabLifecycleHost.HasFullScreenElement(BrowserTab tab)
        => GetTabWebView(tab)?.CoreWebView2?.ContainsFullScreenElement == true;
    bool IClosedTabLifecycleHost.CanRememberClosedTab(BrowserTab tab)
        => !_isPrivateWindow
            && (!tab.IsNativeContentTab || tab.IsFolderTab)
            && !string.IsNullOrWhiteSpace(tab.Url);
    void IClosedTabLifecycleHost.SilenceTabRuntimeAudio(BrowserTab tab) => SilenceTabRuntimeAudio(tab);
    void IClosedTabLifecycleHost.ClearSplitViewForSingleTab(BrowserTab tab) => ClearSplitViewForSingleTab(tab);
    void IClosedTabLifecycleHost.PersistSplitViewState() => PersistSplitViewState();
    void IClosedTabLifecycleHost.RefreshVerticalTabViews() => RefreshVerticalTabViews();
    void IClosedTabLifecycleHost.QueueSessionSave() => QueueSessionSave();
    void IClosedTabLifecycleHost.ApplyYouTubeApplicationFullscreen(bool enterFullscreen)
        => ApplyYouTubeApplicationFullscreen(enterFullscreen);
    ClosedTabState IClosedTabLifecycleHost.CreateClosedTabState(BrowserTab tab, DateTimeOffset closedAt, bool isMuted)
        => CreateClosedTabState(tab, closedAt, isMuted);
    bool IClosedTabLifecycleHost.CanPreserveClosedTabRuntime(BrowserTab tab) => CanPreserveClosedTabRuntime(tab);
    void IClosedTabLifecycleHost.BeginClosedTabThumbnailCapture(ClosedTabState closedTab, BrowserTab tab)
        => BeginClosedTabThumbnailCapture(closedTab, tab);
    void IClosedTabLifecycleHost.RefreshClosedTabHistoryViews() => RefreshClosedTabHistoryViews();
    void IClosedTabLifecycleHost.QueueClosedTabNativeAudioPanReset(BrowserTab tab) => QueueClosedTabNativeAudioPanReset(tab);
    void IClosedTabLifecycleHost.RemoveWebViewFromHosts(BrowserTab tab) => RemoveWebViewFromHosts(tab);
    void IClosedTabLifecycleHost.ClearRemovedTabWindowState(BrowserTab tab)
    {
        _newWindowPopupTabIds.Remove(tab.Id);
        _youTubeControlWorkCoordinator.ClearDisplayFallback(tab.Id);
        _youTubeControlService.RemoveTab(tab);
    }
    void IClosedTabLifecycleHost.DetachPreservedTabRuntime(BrowserTab tab) => DetachPreservedTabRuntime(tab);
    void IClosedTabLifecycleHost.ResumeDeferredAutomaticBackgroundWorkIfPlaybackStopped()
        => ResumeDeferredAutomaticBackgroundWorkIfPlaybackStopped();
    void IClosedTabLifecycleHost.QueuePreservedClosedTabSuspension(BrowserTab tab) => QueuePreservedClosedTabSuspension(tab);
    void IClosedTabLifecycleHost.TerminateTabRuntime(BrowserTab tab) => TerminateTabRuntime(tab);
    void IClosedTabLifecycleHost.AddNewTab(bool activate) => AddNewTab(activate: activate);
    void IClosedTabLifecycleHost.CloseWindow() => Close();
    void IClosedTabLifecycleHost.CloseTab(BrowserTab tab) => CloseTab(tab);
    void IClosedTabLifecycleHost.SelectTab(BrowserTab tab) => SelectTab(tab);
    void IClosedTabLifecycleHost.QueueSelectedTabActivation() => QueueSelectedTabActivation();
    void IClosedTabLifecycleHost.QueueWorkspaceOpenTabsRefresh() => QueueWorkspaceOpenTabsRefresh();
    BrowserTab IClosedTabLifecycleHost.CreateRestoredFolderTab(ClosedTabState closedTab) => CreateRestoredFolderTab(closedTab);
    BrowserTab IClosedTabLifecycleHost.CreateRestoredWebTab(ClosedTabState closedTab)
    {
        NoteExplorationActivity();
        var state = BrowserTabStateMapper.FromClosedTabState(closedTab);
        var url = NormalizeYouTubeShortsUrl(NormalizeSessionTabUrl(state.Url, state.Title) ?? NewTabUrl);
        var tab = BrowserTabStateMapper.CreateBrowserTab(state, url);
        ApplyContainerProfileToTab(tab);
        RestoreNavigationHistory(tab, state.NavigationHistory, state.NavigationHistoryIndex, NormalizeSessionTabUrl(url, state.Title));
        return tab;
    }
    void IClosedTabLifecycleHost.SetStatus(string message, bool autoClear) => SetStatus(message, autoClear);
    void IClosedTabLifecycleHost.AttachTransferredWebView(BrowserTab tab) => AttachTransferredWebView(tab);
    void IClosedTabLifecycleHost.ResumeTabIfSuspended(BrowserTab tab) => ResumeTabIfSuspended(tab);
    void IClosedTabLifecycleHost.RestoreTabRuntimeAudioState(BrowserTab tab, bool isMuted) => RestoreTabRuntimeAudioState(tab, isMuted);
    void IClosedTabLifecycleHost.RefreshTabFromCore(BrowserTab tab) => RefreshTabFromCore(tab);
    void IClosedTabLifecycleHost.QueueRestoredTabBridgeCompatibilityProbe(BrowserTab tab) => QueueRestoredTabBridgeCompatibilityProbe(tab);
    void IClosedTabLifecycleHost.QueueBackgroundTabLoading() => QueueBackgroundTabLoading();
    Task<bool> IClosedTabLifecycleHost.TrySuspendTabAsync(BrowserTab tab) => TrySuspendTabAsync(tab);

    private void DisposeWindowTabRuntimes()
    {
        YouTubeControlPopup.IsOpen = false;
        HideSplitYouTubeControlPopups();
        DisposeFloatingMediaOverlays();
        foreach (var tab in _tabs.ToList())
        {
            TerminateTabRuntime(tab);
        }
    }

    private void ReleaseTabRuntimeResources(BrowserTab tab)
    {
        s_tabRuntimeCoordinator.ReleaseResources(
            tab,
            _ => ReleaseTabRuntimeResourcesCore(tab),
            () => _webViewLifecycleService.ResetRuntimeState(tab));
    }

    private void TerminateTabRuntime(BrowserTab tab)
    {
        s_tabRuntimeCoordinator.Terminate(
            tab,
            resources => PrepareTabRuntimeTermination(tab, resources),
            () => _webViewLifecycleService.ResetRuntimeState(tab));
    }

    private void PrepareTabRuntimeTermination(BrowserTab tab, TabRuntimeResources resources)
    {
        List<Exception>? exceptions = null;
        void RunCleanup(Action cleanup)
        {
            try
            {
                cleanup();
            }
            catch (Exception ex)
            {
                (exceptions ??= []).Add(ex);
            }
        }

        if (resources.HostedContent is UIElement hostedElement)
        {
            RunCleanup(() => DetachHostedContent(hostedElement));
        }

        RunCleanup(() => ReleaseTabRuntimeResourcesCore(tab));
        ThrowIfCleanupFailed("タブの終端準備をすべて完了できませんでした。", exceptions);
    }

    private void ReleaseTabRuntimeResourcesCore(BrowserTab tab)
    {
        List<Exception>? exceptions = null;
        void RunCleanup(Action cleanup)
        {
            try
            {
                cleanup();
            }
            catch (Exception ex)
            {
                (exceptions ??= []).Add(ex);
            }
        }

        RunCleanup(() => CancelSitePermissionPromptsForTab(tab.Id));
        RunCleanup(() => ClosePageFindForTab(tab));
        RunCleanup(() => CancelPageZoomNavigation(tab));
        RunCleanup(() => ResetYouTubePlaybackDiagnostic("tab", tab.Id));
        RunCleanup(() => CancelInactiveYouTubeTabCheck(tab));
        _inactiveYouTubeTabWorkCoordinator.Remove(tab.Id);
        RunCleanup(() => RemoveWebViewFromHosts(tab));
        RunCleanup(() => s_tabAudioPanFrameSubscriptionOwner.Dispose(tab));
        ThrowIfCleanupFailed("タブのRuntime資源をすべて解放できませんでした。", exceptions);
    }

    private static void ThrowIfCleanupFailed(string message, List<Exception>? exceptions)
    {
        if (exceptions is { Count: 1 })
        {
            throw exceptions[0];
        }

        if (exceptions is { Count: > 1 })
        {
            throw new AggregateException(message, exceptions);
        }
    }

    private void MoveTabSelection(int offset) => _tabSelectionController.MoveTabSelection(offset);

    private void UpdateRightWheelTabSelectionInformation(BrowserTab tab)
    {
        SetAddressText(tab.IsNativeContentTab || IsNewTabUrl(tab.Url)
            ? string.Empty
            : tab.Url ?? string.Empty);
    }

    IList<BrowserTab> ITabSelectionHost.Tabs => _tabs;
    BrowserTab? ITabSelectionHost.ActiveTab => ActiveTab;
    bool ITabSelectionHost.IsClosing => _isClosingWindow;
    bool ITabSelectionHost.IsRightWheelCycling => IsRightWheelTabCyclingActive();
    void ITabSelectionHost.NoteExplorationActivity() => NoteExplorationActivity();
    void ITabSelectionHost.RestoreApplicationFullscreenBeforeTabSelectionChange()
        => RestoreApplicationFullscreenBeforeTabSelectionChange();
    void ITabSelectionHost.SelectTab(BrowserTab tab) => SelectTab(tab);
    void ITabSelectionHost.UpdateRightWheelTabSelectionInformation(BrowserTab tab)
        => UpdateRightWheelTabSelectionInformation(tab);
    void ITabSelectionHost.QueueSelectedTabActivation() => QueueSelectedTabActivation();
    void ITabSelectionHost.QueueActivationCheck(Action action)
        => Dispatcher.BeginInvoke(action, DispatcherPriority.Background);
    bool ITabSelectionHost.HasLoadedRuntime(BrowserTab tab) => _webViewLifecycleService.HasLoadedRuntime(tab);
    void ITabSelectionHost.ShowMemoryRestorePlaceholder(BrowserTab tab) => ShowMemoryRestorePlaceholder(tab);
    void ITabSelectionHost.QueueSettledActivation(Func<CancellationToken, Task> operation)
        => _windowTaskSupervisor.TryRun("right-wheel-tab-activation-settle", operation);
    Task ITabSelectionHost.ActivateSelectedTabAsync(CancellationToken cancellationToken)
        => ActivateSelectedTabAsync(cancellationToken);
    void ITabSelectionHost.QueueBackgroundTabLoading() => QueueBackgroundTabLoading();
    void ITabSelectionHost.QueueBackgroundThumbnailWarmup() => QueueBackgroundThumbnailWarmup();

    private void ReorderTab(BrowserTab draggedTab, Point dropPoint, object originalSource)
        => _tabReorderController.ReorderTab(draggedTab, dropPoint, originalSource);

    private void ConfirmReorderDrop(BrowserTab draggedTab, Point dropPoint, object originalSource)
        => _tabReorderController.ConfirmReorderDrop(draggedTab, dropPoint, originalSource);

    private void ConfirmReorderDrop(
        BrowserTab draggedTab,
        ListBox tabList,
        Point dropPoint,
        object originalSource)
        => _tabReorderController.ConfirmReorderDrop(draggedTab, tabList, dropPoint, originalSource);

    private bool PreviewReorderTab(BrowserTab draggedTab, Point dropPoint, object originalSource)
        => _tabReorderController.PreviewReorderTab(draggedTab, dropPoint, originalSource);

    private bool PreviewReorderTab(
        BrowserTab draggedTab,
        ListBox tabList,
        Point dropPoint,
        object originalSource)
        => _tabReorderController.PreviewReorderTab(draggedTab, tabList, dropPoint, originalSource);

    private IReadOnlyList<BrowserTab> GetTabDragGroup(BrowserTab tab)
        => _tabReorderController.GetTabDragGroup(tab);

    private SplitViewModel? GetSplitViewSnapshotForTab(BrowserTab tab)
    {
        return _splitViewService
            .Snapshot()
            .FirstOrDefault(splitView => splitView.LeftTabId == tab.Id || splitView.RightTabId == tab.Id);
    }

    private int GetDropTargetIndex(BrowserTab draggedTab, Point dropPoint, object originalSource)
        => _tabReorderController.GetDropTargetIndex(draggedTab, dropPoint, originalSource);

    private int GetInsertionIndexForDrop(Point dropPoint, object originalSource)
        => _tabReorderController.GetInsertionIndexForDrop(dropPoint, originalSource);

    private int GetInsertionIndexForDrop(ListBox tabList, Point dropPoint, object originalSource)
        => _tabReorderController.GetInsertionIndexForDrop(tabList, dropPoint, originalSource);

    private bool UpdateDropPreview(Point dropPoint, object originalSource)
        => _tabReorderController.UpdateDropPreview(dropPoint, originalSource);

    private bool UpdateDropPreview(ListBox tabList, Point dropPoint, object originalSource)
        => _tabReorderController.UpdateDropPreview(tabList, dropPoint, originalSource);

    private void ClearTabDragStatus()
    {
        if (!_statusPresentationController.StatusText.StartsWith(TabDragStatusPrefix, StringComparison.Ordinal))
        {
            return;
        }

        _statusPresentationController.ClearStatusIfCurrent(_statusPresentationController.StatusText);
    }

    private void ResetWorkspaceTabDragHover()
        => _tabDropController.ResetWorkspaceTabDragHover();

    private void RestoreOriginalTabOrder()
        => _tabDragController.RestoreOriginalTabOrder();

    private void ClearDropPreview(bool animateExternalPreview = true)
        => _tabReorderController?.ClearDropPreview(animateExternalPreview);

    private void ClearDropPreviewWithFollowUp(bool animateExternalPreview = true)
        => _tabReorderController?.ClearDropPreviewWithFollowUp(animateExternalPreview);

    private void QueueDropPreviewClear()
        => _tabReorderController?.QueueDropPreviewClear();

    private Dictionary<BrowserTab, Rect> CaptureTabLayoutPositions()
    {
        return CaptureTabLayoutPositions(GetActiveTabDragList());
    }

    private Dictionary<BrowserTab, Rect> CaptureTabLayoutPositions(ListBox tabList)
        => _tabStripController.CaptureTabLayoutPositions(tabList);

    private void AnimateTabLayoutFrom(IReadOnlyDictionary<BrowserTab, Rect> previousPositions)
    {
        AnimateTabLayoutFrom(GetActiveTabDragList(), previousPositions);
    }

    private void AnimateTabLayoutFrom(
        ListBox tabList,
        IReadOnlyDictionary<BrowserTab, Rect> previousPositions)
        => _tabStripController?.AnimateTabLayoutFrom(tabList, previousPositions);

    private void UpdateExternalTabDropPreview(
        ListBox tabList,
        int insertionIndex,
        int draggedTabCount)
        => _tabReorderController.UpdateExternalTabDropPreview(tabList, insertionIndex, draggedTabCount);

    private void UpdateSourceDrivenExternalTabDropPreview()
    {
        if (_activeDragPayload is not { } payload
            || payload.SourceWindow != this
            || !_tabs.Contains(payload.Tab))
        {
            ClearSourceDrivenExternalTabDropPreview();
            return;
        }

        MainWindow? nextPreviewWindow = null;
        foreach (var targetWindow in Application.Current.Windows.OfType<MainWindow>())
        {
            if (targetWindow == this
                || !targetWindow.IsVisible
                || targetWindow.WindowState == WindowState.Minimized
                || !targetWindow.TryGetCursorPointInAnyTabList(out var targetTabList, out var tabListPoint)
                || targetWindow.IsPointOutsideTabReorderDropZone(targetTabList, tabListPoint)
                || !targetWindow.CanDropTabOnList(payload.Tab, targetTabList))
            {
                continue;
            }

            nextPreviewWindow = targetWindow;
            break;
        }

        if (_sourceDrivenExternalTabDropPreviewWindow is { } previousPreviewWindow
            && previousPreviewWindow != nextPreviewWindow)
        {
            previousPreviewWindow.ClearDropPreview();
        }

        _sourceDrivenExternalTabDropPreviewWindow = nextPreviewWindow;
        if (nextPreviewWindow is null
            || !nextPreviewWindow.TryGetCursorPointInAnyTabList(out var previewTabList, out var previewPoint))
        {
            return;
        }

        nextPreviewWindow.MarkTabReorderCandidate();
        var insertionIndex = nextPreviewWindow.GetInsertionIndexForDrop(
            previewTabList,
            previewPoint,
            previewTabList);
        nextPreviewWindow.UpdateDropPreview(previewTabList, previewPoint, previewTabList);
        nextPreviewWindow.UpdateExternalTabDropPreview(
            previewTabList,
            insertionIndex,
            GetTabDragGroup(payload.Tab).Count);
    }

    private void ClearSourceDrivenExternalTabDropPreview()
    {
        if (_sourceDrivenExternalTabDropPreviewWindow is not { } previewWindow)
        {
            return;
        }

        _sourceDrivenExternalTabDropPreviewWindow = null;
        previewWindow.ClearDropPreview();
    }

    private bool ShouldSuppressTabDetachForWorkspaceDrop()
    {
        return _tabDropController.WorkspaceHoverActivated
            && ActiveTab is { IsWorkspaceTab: true };
    }

    private void ResetTabDetachCandidate()
        => _tabDragController.ResetTabDetachCandidate();

    private void MarkTabReorderCandidate()
        => _tabDragController.MarkTabReorderCandidate();

    private bool TryTransferTabToOtherWindowAtCursor(TabDragPayload payload)
    {
        if (payload.SourceWindow != this || !_tabs.Contains(payload.Tab))
        {
            return false;
        }

        foreach (var targetWindow in Application.Current.Windows.OfType<MainWindow>())
        {
            if (targetWindow == this || !targetWindow.IsVisible)
            {
                continue;
            }

            if (!targetWindow.TryGetCursorPointInAnyTabList(out var targetTabList, out var tabListPoint)
                || targetWindow.IsPointOutsideTabReorderDropZone(targetTabList, tabListPoint)
                || !targetWindow.CanDropTabOnList(payload.Tab, targetTabList))
            {
                continue;
            }

            if (targetWindow.InsertExternalTab(payload, targetTabList, tabListPoint, targetTabList))
            {
                return true;
            }
        }

        return false;
    }

    private bool InsertExternalTab(TabDragPayload payload, Point dropPoint, object originalSource)
    {
        return InsertExternalTab(payload, ResolveTabList(originalSource), dropPoint, originalSource);
    }

    private bool InsertExternalTab(
        TabDragPayload payload,
        ListBox tabList,
        Point dropPoint,
        object originalSource)
    {
        if (!CanDropTabOnList(payload.Tab, tabList))
        {
            return false;
        }

        return InsertExternalTabAtIndex(
            payload,
            GetInsertionIndexForDrop(tabList, dropPoint, originalSource),
            animationList: tabList);
    }

    private bool InsertExternalTabAtIndex(
        TabDragPayload payload,
        int insertionIndex,
        bool activateTransferredTab = true,
        ListBox? animationList = null)
    {
        if (payload.SourceWindow == this
            || payload.Tab.IsWorkspaceTab
            || !payload.SourceWindow._tabs.Contains(payload.Tab))
        {
            return false;
        }

        if (payload.SourceWindow._isPrivateWindow != _isPrivateWindow)
        {
            SetStatus("通常WindowとプライベートWindowの間ではタブを移動できません", autoClear: true);
            return false;
        }

        return _tabTransferController.InsertAcceptedExternalTabAtIndex(
            payload,
            insertionIndex,
            activateTransferredTab,
            tab => CaptureTabTransferLayoutAnimation(animationList ?? GetVisibleTabListForTab(tab)),
            () => CaptureTabTransferLayoutAnimation(animationList ?? GetActiveTabDragList()));
    }

    private Action CaptureTabTransferLayoutAnimation(ListBox tabList)
    {
        var previousPositions = CaptureTabLayoutPositions(tabList);
        return () => AnimateTabLayoutFrom(tabList, previousPositions);
    }

    private bool TryTransferTabShelfItem(TabShelfWindowTransferRequest request)
        => _tabTransferController.TryTransferShelfItem(request);

    TabTransferController? ITabTransferHost.FindShelfTargetWindow(Guid windowId)
        => Application.Current.Windows.OfType<MainWindow>()
            .FirstOrDefault(window => window._workspaceWindowId == windowId)?._tabTransferController;
    ITabTransferHost? ITabTransferHost.FindShelfSourceWindow(Guid tabId)
        => Application.Current.Windows.OfType<MainWindow>()
            .FirstOrDefault(window => window._tabs.Any(tab => tab.Id == tabId));
    bool ITabTransferHost.CanTransferShelfTab(ITabTransferHost source, BrowserTab sourceTab)
    {
        var sourceWindow = (MainWindow)source;
        var isSameWindow = ReferenceEquals(sourceWindow, this);
        var hasMatchingPrivacyMode = sourceWindow._isPrivateWindow == _isPrivateWindow;
        if (!_tabDragDropService.CanTransferShelfTab(
                sourceTab.IsWorkspaceTab,
                isSameWindow,
                hasMatchingPrivacyMode))
        {
            var message = sourceTab.IsWorkspaceTab
                ? "Workspace タブは別ウィンドウへ移動できません"
                : isSameWindow
                    ? "同じウィンドウ内では通常のタブ列で並べ替えてください"
                    : "通常WindowとプライベートWindowの間ではタブを移動できません";
            SetStatus(message, autoClear: true);
            return false;
        }

        return true;
    }

    bool ITabTransferHost.InsertExternalShelfTab(ITabTransferHost source, BrowserTab tab, int insertionIndex)
        => InsertExternalTabAtIndex(new TabDragPayload((MainWindow)source, tab), insertionIndex, activateTransferredTab: false);
    void ITabTransferHost.ClearDetachedToWindow() => _tabDragController.ClearDetachedToWindow();
    void ITabTransferHost.RefreshLoadedWorkspaceOpenTabs() => RefreshLoadedWorkspaceOpenTabs();

    IList<BrowserTab> ITabTransferRecoveryHost.Tabs => _tabs;
    Guid ITabTransferRecoveryHost.WindowId => _workspaceWindowId;
    BrowserTab? ITabTransferRecoveryHost.ActiveTab => ActiveTab;
    void ITabTransferRecoveryHost.CleanupTransferredTargetTab(BrowserTab tab) => _tabTransferController.CleanupTransferredTargetTab(tab);
    void ITabTransferRecoveryHost.InsertPreservedTab(BrowserTab tab, int index, bool activate)
        => InsertPreservedTab(tab, index, activate);
    void ITabTransferRecoveryHost.ParkFolderTabContent(BrowserTab tab)
    {
        if (GetTabHostedContent(tab) is UIElement content) ParkFolderTabContent(content);
    }
    void ITabTransferRecoveryHost.AttachTransferredWebView(BrowserTab tab) => AttachTransferredWebView(tab);
    void ITabTransferRecoveryHost.RecordRuntimeTransitionFailure(BrowserTab tab, string operation, TabRuntimeTransitionResult result)
        => RecordTabRuntimeTransitionFailure(tab, operation, result);
    void ITabTransferRecoveryHost.SelectTab(BrowserTab tab) => SelectTab(tab);
    void ITabTransferRecoveryHost.QueueSelectedTabActivation() => QueueSelectedTabActivation();
    void ITabTransferRecoveryHost.PersistSplitViewState() => PersistSplitViewState();
    void ITabTransferRecoveryHost.RefreshSplitViewTabVisualStates() => RefreshSplitViewTabVisualStates();
    void ITabTransferRecoveryHost.RefreshVerticalTabViews() => RefreshVerticalTabViews();
    void ITabTransferRecoveryHost.QueueSessionSave() => QueueSessionSave();
    void ITabTransferRecoveryHost.QueueWorkspaceOpenTabsRefresh() => QueueWorkspaceOpenTabsRefresh();
    void ITabTransferRecoveryHost.RecordRollbackErrors(string operation, IReadOnlyList<Exception> errors)
        => RecordTabTransferRollbackErrors(operation, errors);

    private void RecordTabTransferRollbackErrors(
        string operation,
        IReadOnlyList<Exception> errors)
    {
        foreach (var error in errors)
        {
            AppendDiagnosticLogLine(
                "runner=tab-transfer",
                $"operation={NormalizeSessionDiagnosticToken(operation)}",
                $"window={_workspaceWindowId:N}",
                $"error={NormalizeSessionDiagnosticToken(error.Message)}");
        }
    }

    private void InsertPreservedTab(BrowserTab tab, int insertionIndex, bool activate)
        => _tabTransferController.InsertPreservedTab(tab, insertionIndex, activate);

    private void AttachTransferredWebView(BrowserTab tab)
    {
        if (GetTabWebView(tab) is not { } webView)
        {
            return;
        }

        if (webView.CoreWebView2 is not { } core)
        {
            RebindWebViewControlEventHandlers(tab, webView);
            return;
        }

        RebindWebViewRuntimeEventHandlers(tab, webView, core);
        RefreshTabFromCore(tab);
        RefreshTabAudioState(tab);
        RefreshTabMuteState(tab);
        QueueExtensionToolbarRefresh(core.Profile);
    }

    private bool InsertTransferredTab(TabTransferSnapshot snapshot, Point dropPoint, object originalSource)
    {
        return InsertTransferredTab(snapshot, ResolveTabList(originalSource), dropPoint, originalSource);
    }

    private bool InsertTransferredTab(
        TabTransferSnapshot snapshot,
        ListBox tabList,
        Point dropPoint,
        object originalSource)
    {
        if (snapshot.IsPrivateWindow != _isPrivateWindow)
        {
            SetStatus("通常WindowとプライベートWindowの間ではタブを移動できません", autoClear: true);
            return false;
        }

        var url = NormalizeYouTubeShortsUrl(string.IsNullOrWhiteSpace(snapshot.Url) ? NewTabUrl : snapshot.Url);
        if (IsWorkspaceTabUrl(url))
        {
            SetStatus("Workspace タブは別ウィンドウへ移動できません", autoClear: true);
            return false;
        }

        var insertionIndex = GetInsertionIndexForDrop(tabList, dropPoint, originalSource);
        var newTab = new BrowserTab
        {
            Id = Guid.NewGuid(),
            Url = url,
            Title = snapshot.Title,
            ContainerProfileId = ContainerProfile.NormalizeId(snapshot.ContainerProfileId)
        };
        ApplyContainerProfileToTab(newTab);

        return _tabTransferController.InsertAcceptedSnapshotTab(
            newTab,
            insertionIndex,
            () => CaptureTabTransferLayoutAnimation(tabList));
    }

    private void OpenExternalTabDropInNewTab(
        ExternalTabDropTarget externalDrop,
        ListBox tabList,
        Point dropPoint,
        object originalSource)
    {
        var searchText = externalDrop.Value;
        if (externalDrop.Kind == ExternalTabDropKind.Search
            && searchText.Length > SearchSelectedTextMaxLength)
        {
            searchText = searchText[..SearchSelectedTextMaxLength];
        }

        var url = externalDrop.Kind == ExternalTabDropKind.Search
            ? _addressService.CreateSearchUrl(searchText)
            : externalDrop.Value;
        var insertionIndex = GetInsertionIndexForDrop(tabList, dropPoint, originalSource);
        var previousPositions = CaptureTabLayoutPositions(tabList);
        OpenTab(
            url,
            activate: true,
            title: externalDrop.Kind == ExternalTabDropKind.Search ? searchText : null,
            insertionIndex: insertionIndex,
            containerProfileId: externalDrop.Kind == ExternalTabDropKind.Search
                ? GetActiveBrowserCommandTarget()?.ContainerProfileId
                : null);
        AnimateTabLayoutFrom(tabList, previousPositions);
        SetStatus(
            externalDrop.Kind == ExternalTabDropKind.Search
                ? "選択した文字列を検索しました"
                : "ドロップしたリンクを開きました",
            autoClear: true);
    }

    private void DetachTabToNewWindow(BrowserTab tab, TabDragPayload dragPayload)
    {
        if (!_tabs.Contains(tab))
        {
            return;
        }

        if (tab.IsWorkspaceTab)
        {
            SelectTab(tab);
            SetStatus("Workspace タブは新しいウィンドウへ分離できません", autoClear: true);
            return;
        }

        _tabTransferController.DetachAcceptedTabToNewWindow(tab, dragPayload);
    }

    private void StartFollowingDragCursor()
    {
        _isFollowingDragCursor = true;
        PositionNearDragCursor();
        _dragWindowFollowTimer.Start();
    }

    private void UpdateDragWindowFollowPosition()
    {
        if (!_isFollowingDragCursor)
        {
            _dragWindowFollowTimer.Stop();
            return;
        }

        if (!IsLeftMouseButtonDown())
        {
            _isFollowingDragCursor = false;
            _dragWindowFollowTimer.Stop();
            return;
        }

        PositionNearDragCursor();
    }

    private void PositionNearDragCursor()
    {
        WindowStartupLocation = WindowStartupLocation.Manual;

        var cursor = GetCursorScreenPoint();
        var width = Math.Max(MinWidth, Width);
        var height = Math.Max(MinHeight, Height);

        Left = ClampToVirtualScreen(
            cursor.X - 140,
            width,
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenWidth);
        Top = ClampToVirtualScreen(
            cursor.Y - 24,
            height,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenHeight);
    }

    bool ITabTransferHost.IsLoaded => IsLoaded;
    void ITabTransferHost.CloseTabContextMenuForTab(BrowserTab tab) => CloseTabContextMenuForTab(tab);
    Func<TabTransferController> ITabTransferHost.CaptureNewTransferWindowFactory()
    {
        var cursor = GetCursorScreenPoint();
        return () =>
        {
            var newWindow = new MainWindow(
                initialTabs: null,
                persistSession: false,
                initialPosition: cursor,
                isPrivateWindow: _isPrivateWindow);
            return newWindow._tabTransferController;
        };
    }
    Action ITabTransferHost.CaptureDefaultTransferLayoutAnimation()
        => CaptureTabTransferLayoutAnimation(GetActiveTabDragList());
    void ITabTransferHost.PositionNearDragCursor() => PositionNearDragCursor();
    void ITabTransferHost.ShowWindow() => Show();
    bool ITabTransferHost.IsLeftMouseButtonDown() => IsLeftMouseButtonDown();
    void ITabTransferHost.StartFollowingDragCursor() => StartFollowingDragCursor();
    void ITabTransferHost.RestoreUnpreparedTabRuntimeTransfers(IReadOnlyList<PendingTabRuntimeTransfer> transfers)
        => _tabTransferController.Recovery.RollbackTabRuntimeTransfers(transfers, this);

    IReadOnlyList<BrowserTab> ITabTransferHost.GetTabDragGroup(BrowserTab tab) => GetTabDragGroup(tab);
    SplitViewModel? ITabTransferHost.GetSplitViewSnapshotForTab(BrowserTab tab) => GetSplitViewSnapshotForTab(tab);
    FolderTabTransferSnapshot ITabTransferHost.CaptureTransferredFolderTabSnapshot(BrowserTab tab)
        => CaptureTransferredFolderTabSnapshot(tab);
    BrowserTab ITabTransferHost.CreateTransferredFolderTab(FolderTabTransferSnapshot snapshot)
        => CreateTransferredFolderTab(snapshot);
    bool ITabTransferHost.TryBeginTabRuntimeTransfers(ITabTransferHost source, IEnumerable<BrowserTab> tabs,
        out IReadOnlyList<PendingTabRuntimeTransfer> transfers)
        => _tabTransferController.Recovery.TryBeginTabRuntimeTransfers(source, tabs, out transfers);
    void ITabTransferHost.CommitTabRuntimeTransfersOrThrow(IReadOnlyList<PendingTabRuntimeTransfer> transfers)
        => _tabTransferController.Recovery.CommitTabRuntimeTransfersOrThrow(transfers);
    void ITabTransferHost.RollbackTabRuntimeTransfers(IReadOnlyList<PendingTabRuntimeTransfer> transfers, ITabTransferHost source)
        => _tabTransferController.Recovery.RollbackTabRuntimeTransfers(transfers, source, restorePhysicalOwnership: false);
    bool ITabTransferHost.AreTabRuntimeTransfersRestored(IReadOnlyList<PendingTabRuntimeTransfer> transfers, ITabTransferHost source)
        => _tabTransferController.Recovery.AreTabRuntimeTransfersRestored(transfers, source);
    void ITabTransferHost.RegisterExternalTabMoveUndo(ITabTransferPayload payload, BrowserTab targetTab, bool closeTargetWindowAfterUndo)
        => RegisterExternalTabMoveUndo((TabDragPayload)payload, targetTab, closeTargetWindowAfterUndo);
    void ITabTransferHost.SetTransferStatus(string message) => SetStatus(message, autoClear: true);

    bool ITabTransferHost.PersistSession => _persistSession;
    void ITabTransferHost.ApplyContainerProfileToTab(BrowserTab tab) => ApplyContainerProfileToTab(tab);
    int ITabTransferHost.ClampNormalTabInsertionIndex(int index) => ClampNormalTabInsertionIndex(index);
    bool ITabTransferHost.HasLoadedRuntime(BrowserTab tab) => _webViewLifecycleService.HasLoadedRuntime(tab);
    void ITabTransferHost.ShowWebView(BrowserTab tab) => ShowWebView(tab);
    void ITabTransferHost.MoveWebViewToBackground(BrowserTab tab) => MoveWebViewToBackground(tab);
    void ITabTransferHost.QueueBackgroundTabLoading() => QueueBackgroundTabLoading();
    void ITabTransferHost.RestoreOriginalTabOrder() => RestoreOriginalTabOrder();
    void ITabTransferHost.MarkDetachedToWindow() => _tabDragController.MarkDetachedToWindow();
    void ITabTransferHost.TerminateTabRuntime(BrowserTab tab) => TerminateTabRuntime(tab);
    void ITabTransferHost.AddNewTab(bool activate) => AddNewTab(activate);
    void ITabTransferHost.CloseWindow() => Close();
    void ITabTransferHost.QueueCloseAfterTransferredTabsRemoved() => QueueCloseAfterTransferredTabsRemoved();
    void ITabTransferHost.ClearSplitViewForSingleTab(BrowserTab tab) => ClearSplitViewForSingleTab(tab);
    void ITabTransferHost.DetachTransferredFolderContent(BrowserTab tab)
    {
        if (GetTabHostedContent(tab) is UIElement hostedContent)
        {
            DetachHostedContent(hostedContent);
        }
    }
    void ITabTransferHost.RemoveWebViewFromHosts(BrowserTab tab) => RemoveWebViewFromHosts(tab);
    void ITabTransferHost.PrepareForRuntimeTransfer(BrowserTab tab) => _webViewLifecycleService.PrepareForRuntimeTransfer(tab);
    void ITabTransferHost.VerifyRemovedTransferredTabRuntime(BrowserTab tab) => VerifyRemovedTransferredTabRuntime(tab);
    void ITabTransferHost.ForgetTabThumbnailCapture(Guid tabId) => _tabThumbnailCaptureController.ForgetCapture(tabId);
    void ITabTransferHost.ClearYouTubeDisplayFallback(Guid tabId) => _youTubeControlWorkCoordinator.ClearDisplayFallback(tabId);
    void ITabTransferHost.RemoveYouTubeControlTab(BrowserTab tab) => _youTubeControlService.RemoveTab(tab);
    void ITabTransferHost.HideDetachedSplitView() => SplitViewHost.Visibility = Visibility.Collapsed;
    void ITabTransferHost.HideSplitYouTubeControlPopups() => HideSplitYouTubeControlPopups();

    private void QueueCloseAfterTransferredTabsRemoved()
    {
        if (_isClosingWindow
            || _persistSession
            || _tabs.Count != 0
            || Dispatcher.HasShutdownStarted
            || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_isClosingWindow
                && !_persistSession
                && _tabs.Count == 0)
            {
                Close();
            }
        }), DispatcherPriority.ContextIdle);
    }

    private static bool HasPlayingYouTubePlaybackAcrossWindows()
    {
        return GetOpenMainWindows().Any(window =>
            window._tabs.Any(tab =>
                window.IsYouTubeVideoTab(tab)
                && window.TryGetCurrentYouTubePlaybackPaused(tab) == false)
            || window.HasPlayingFloatingMedia());
    }

    private static void ResumeDeferredAutomaticBackgroundWorkIfPlaybackStopped()
    {
        if (HasPlayingYouTubePlaybackAcrossWindows())
        {
            return;
        }

        foreach (var window in GetOpenMainWindows())
        {
            window._backgroundTabWorkController.ResumeDeferredAutomaticWork();
        }
    }

    private void QueueBackgroundTabLoading() => _backgroundTabWorkController.QueueBackgroundTabLoading();
    private void QueueUserRequestedBackgroundTabLoading(BrowserTab tab) => _backgroundTabWorkController.QueueUserRequestedBackgroundTabLoading(tab);
    private void DeferBackgroundTabLoading() => _backgroundTabWorkController.DeferBackgroundTabLoading();
    private void DeferBackgroundWork(int delayMilliseconds) => _backgroundTabWorkController.DeferBackgroundWork(delayMilliseconds);
    private void QueueBackgroundThumbnailWarmup() => _backgroundTabWorkController.QueueBackgroundThumbnailWarmup();

    bool IBackgroundTabWorkHost.IsInitialized => _isInitialized;
    bool IBackgroundTabWorkHost.IsClosing => _isClosingWindow;
    bool IBackgroundTabWorkHost.IsFocusedViewingCpuSaverActive => _inactiveTabWorkController.IsCpuSaverActive;
    BrowserTab? IBackgroundTabWorkHost.ActiveTab => ActiveTab;
    IList<BrowserTab> IBackgroundTabWorkHost.Tabs => _tabs;
    CancellationToken IBackgroundTabWorkHost.LifetimeToken => _windowTaskSupervisor.LifetimeToken;
    bool IBackgroundTabWorkHost.IsRightWheelTabCyclingActive() => IsRightWheelTabCyclingActive();
    bool IBackgroundTabWorkHost.HasPlayingMediaAcrossWindows() => HasPlayingYouTubePlaybackAcrossWindows();
    bool IBackgroundTabWorkHost.ShouldPauseBackgroundLoad(BrowserTab? activeTab) => _webViewLifecycleService.ShouldPauseBackgroundLoad(activeTab);
    bool IBackgroundTabWorkHost.ShouldPauseBackgroundThumbnailWarmup(BrowserTab? activeTab) => _webViewLifecycleService.ShouldPauseBackgroundThumbnailWarmup(activeTab);
    bool IBackgroundTabWorkHost.IsBackgroundLoadCandidate(BrowserTab tab, BrowserTab? activeTab) => _webViewLifecycleService.IsBackgroundLoadCandidate(tab, activeTab, ShouldDeferBackgroundMediaLoad);
    bool IBackgroundTabWorkHost.IsBackgroundThumbnailCandidate(BrowserTab tab, BrowserTab? activeTab) => _webViewLifecycleService.IsBackgroundThumbnailCandidate(tab, activeTab);
    bool IBackgroundTabWorkHost.HasLoadFailure(BrowserTab tab) => _webViewLifecycleService.HasLoadFailure(tab);
    IReadOnlySet<Guid> IBackgroundTabWorkHost.GetGlobalHotTabIds() => GetGlobalHotTabIds();
    void IBackgroundTabWorkHost.RunBackgroundTask(string operation, Func<Task> action) => _windowTaskSupervisor.TryRun(operation, _ => action());
    void IBackgroundTabWorkHost.StopDelayTimer() => _backgroundLoadDelayTimer.Stop();
    void IBackgroundTabWorkHost.RestartDelayTimer(TimeSpan interval)
    {
        _backgroundLoadDelayTimer.Stop();
        _backgroundLoadDelayTimer.Interval = interval;
        _backgroundLoadDelayTimer.Start();
    }
    Task IBackgroundTabWorkHost.EnsureLoadedAsync(BrowserTab tab, string? url) => EnsureTabLoadedAsync(tab, url);
    Task IBackgroundTabWorkHost.CaptureBackgroundThumbnailAsync(BrowserTab tab) => CaptureTabThumbnailAsync(tab, isBackgroundWarmup: true);
    void IBackgroundTabWorkHost.MarkStartupPerformance(string stage, BrowserTab tab) => MarkStartupPerformance(stage, tab);
    void IBackgroundTabWorkHost.RecordLoadDiagnostic(string category, BrowserTab tab, string? url, long elapsedMilliseconds, string detail) => AppendWebViewPerformanceDiagnostic(category, tab, url, elapsedMilliseconds, detail);
    string? IBackgroundTabWorkHost.CreateRestoredTabLoadFailureStatus() => CreateRestoredTabLoadFailureStatus();
    void IBackgroundTabWorkHost.SetStatus(string status) => SetStatus(status);
    void IBackgroundTabWorkHost.QueueWorkspaceOpenTabsRefresh() => QueueWorkspaceOpenTabsRefresh();

    private IReadOnlySet<Guid> GetGlobalHotTabIds()
    {
        return _tabMemoryManagementService.SelectHotTabIds(CaptureGlobalTabMemorySnapshots());
    }

    private static List<TabMemorySnapshot> CaptureGlobalTabMemorySnapshots()
    {
        var snapshots = new List<TabMemorySnapshot>();
        foreach (var window in GetOpenMainWindows())
        {
            var activeTab = window.ActiveTab;
            foreach (var tab in window._tabs)
            {
                snapshots.Add(new TabMemorySnapshot(
                    tab.Id,
                    tab.LastActivatedAt,
                    ReferenceEquals(tab, activeTab),
                    tab.IsLoaded,
                    window._webViewLifecycleService.HasLoadedRuntime(tab),
                    tab.IsPinned,
                    window.SplitViewHost.Visibility == Visibility.Visible
                        && window._splitViewService.Contains(tab),
                    tab.IsPlayingAudio,
                    tab.IsLoading,
                    window._webViewLifecycleService.IsInitializing(tab),
                    tab.IsNativeContentTab,
                    tab.IsSuspended));
            }
        }

        return snapshots;
    }

    private void NoteExplorationActivity() => _inactiveTabWorkController.NoteExplorationActivity();
    private void RestartFocusedViewingCpuSaverTimer() => _inactiveTabWorkController.RestartFocusedViewingCpuSaverTimer();
    private TimeSpan GetFocusedViewingCpuSaverDelay() => _inactiveTabWorkController.GetFocusedViewingCpuSaverDelay();
    private void HandleYouTubePlaybackActivityChanged(string sourceKind, Guid targetId, YouTubePlaybackDiagnosticMessage message)
        => _inactiveTabWorkController.HandleYouTubePlaybackActivityChanged(sourceKind, targetId, message);
    private void QueueInactiveYouTubeTabCheck(BrowserTab tab, bool stopStalledLoad)
        => _inactiveTabWorkController.QueueInactiveYouTubeTabCheck(tab, stopStalledLoad);
    private void CancelInactiveYouTubeTabCheck(BrowserTab tab) => _inactiveTabWorkController.CancelInactiveYouTubeTabCheck(tab);
    private void ResumeInactiveYouTubeTabForDisplay(BrowserTab tab) => _inactiveTabWorkController.ResumeInactiveYouTubeTabForDisplay(tab);
    private void QueueFocusedViewingCpuSaver() => _inactiveTabWorkController.QueueFocusedViewingCpuSaver();

    bool IInactiveTabWorkHost.IsInitialized => _isInitialized;
    bool IInactiveTabWorkHost.IsClosing => _isClosingWindow;
    bool IInactiveTabWorkHost.IsCpuSaverEnabled => _settings.EnableFocusedViewingCpuSaver;
    double IInactiveTabWorkHost.CpuSaverDelaySeconds => _settings.FocusedViewingCpuSaverDelaySeconds;
    int IInactiveTabWorkHost.WindowIndex => _workspaceWindowIndex;
    BrowserTab? IInactiveTabWorkHost.ActiveTab => ActiveTab;
    IList<BrowserTab> IInactiveTabWorkHost.Tabs => _tabs;
    IReadOnlySet<Guid> IInactiveTabWorkHost.GetGlobalHotTabIds() => GetGlobalHotTabIds();
    bool IInactiveTabWorkHost.IsYouTubeVideoTab(BrowserTab tab) => IsYouTubeVideoTab(tab);
    bool IInactiveTabWorkHost.IsVisibleSplitTab(BrowserTab tab) => SplitViewHost.Visibility == Visibility.Visible && _splitViewService.Contains(tab);
    bool IInactiveTabWorkHost.HasCore(BrowserTab tab) => GetTabWebView(tab)?.CoreWebView2 is not null;
    bool IInactiveTabWorkHost.IsInitializing(BrowserTab tab) => _webViewLifecycleService.IsInitializing(tab);
    Action? IInactiveTabWorkHost.GetStopLoadAction(BrowserTab tab)
        => GetTabWebView(tab)?.CoreWebView2 is { } core ? core.Stop : null;
    Action? IInactiveTabWorkHost.GetReloadAction(BrowserTab tab)
        => GetTabWebView(tab)?.CoreWebView2 is { } core ? core.Reload : null;
    YouTubePlaybackActivitySnapshot? IInactiveTabWorkHost.GetPlaybackActivity(BrowserTab tab)
    {
        TryGetYouTubePlaybackActivity("tab", tab.Id, out var activity);
        return activity;
    }
    bool? IInactiveTabWorkHost.GetCurrentPlaybackPaused(BrowserTab tab) => TryGetCurrentYouTubePlaybackPaused(tab);
    bool IInactiveTabWorkHost.CanSuspendInactiveTab(BrowserTab tab, BrowserTab activeTab, bool isVisibleSplitTab)
        => _webViewLifecycleService.CanSuspendInactiveTab(tab, activeTab, isVisibleSplitTab);
    void IInactiveTabWorkHost.ClearLoading(BrowserTab tab) => _ = s_tabRuntimeCoordinator.SetLoading(tab, false);
    Task<bool> IInactiveTabWorkHost.TrySuspendTabAsync(BrowserTab tab) => TrySuspendTabAsync(tab);
    bool IInactiveTabWorkHost.ResumeTabIfSuspended(BrowserTab tab) => ResumeTabIfSuspended(tab);
    void IInactiveTabWorkHost.RunSupervised(string operation, Func<CancellationToken, Task> action)
        => _windowTaskSupervisor.TryRun(operation, action);
    void IInactiveTabWorkHost.StopFocusedViewingTimer() => _focusedViewingCpuSaverTimer.Stop();
    void IInactiveTabWorkHost.StartFocusedViewingTimer(TimeSpan interval)
    {
        _focusedViewingCpuSaverTimer.Interval = interval;
        _focusedViewingCpuSaverTimer.Start();
    }
    void IInactiveTabWorkHost.StopAutomaticBackgroundWork()
    {
        _backgroundLoadDelayTimer.Stop();
        _backgroundTabWorkCoordinator.ClearAutomaticRequests();
    }
    void IInactiveTabWorkHost.QueueBackgroundTabLoading() => QueueBackgroundTabLoading();
    void IInactiveTabWorkHost.QueueBackgroundThumbnailWarmup() => QueueBackgroundThumbnailWarmup();
    void IInactiveTabWorkHost.QueuePerformanceDiagnosticLogLine(IReadOnlyList<string> fields) => QueuePerformanceDiagnosticLogLine(fields);

    private bool? TryGetCurrentYouTubePlaybackPaused(BrowserTab tab)
    {
        return YouTubeUrlService.TryGetVideoId(tab.Url, out var currentVideoId)
            && string.Equals(
                currentVideoId,
                tab.YouTubePlaybackVideoId,
                StringComparison.Ordinal)
            ? tab.YouTubePlaybackIsPaused
            : null;
    }

    private async Task<bool> TrySuspendTabAsync(BrowserTab tab)
    {
        if (GetTabWebView(tab)?.CoreWebView2 is not { } core)
        {
            return false;
        }

        try
        {
            if (core.IsSuspended)
            {
                _ = s_tabRuntimeCoordinator.SetSuspended(tab, true);
                return false;
            }

            var suspended = await core.TrySuspendAsync();
            _ = s_tabRuntimeCoordinator.SetSuspended(tab, suspended);
            return suspended;
        }
        catch
        {
            return false;
        }
    }

    private bool ResumeTabIfSuspended(BrowserTab tab)
    {
        if (GetTabWebView(tab)?.CoreWebView2 is not { } core)
        {
            _ = s_tabRuntimeCoordinator.SetSuspended(tab, false);
            return false;
        }

        try
        {
            if (!core.IsSuspended)
            {
                _ = s_tabRuntimeCoordinator.SetSuspended(tab, false);
                return false;
            }

            core.Resume();
            _ = s_tabRuntimeCoordinator.SetSuspended(tab, false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ShowWebView(BrowserTab tab)
    {
        if (GetTabWebView(tab) is not { } webView)
        {
            return;
        }

        if (!_splitViewWindowStateCoordinator.IsPreparing && _splitViewService.TryActivateForTab(tab))
        {
            RefreshSplitViewTabVisualStates();
            if (ShowActiveSplitView())
            {
                return;
            }
        }
        else if (_splitViewService.IsActive && !_splitViewService.Contains(tab))
        {
            HideSplitViewForSingleTab(tab);
        }

        SplitViewHost.Visibility = Visibility.Collapsed;
        HideAllFolderTabContents();
        ResumeInactiveYouTubeTabForDisplay(tab);

        if (_displayedTab == tab)
        {
            HidePlaceholder();
            EnsureWebViewHosted(webView);
            ApplyCurrentWebHostSize(webView);
            ParkWebViewInViewport(webView);
            _displayedTab = tab;
            QueueFiveChLiveReadabilityActivation(tab, isActive: true);
            return;
        }

        var previousTab = _displayedTab;
        EnsureWebViewHosted(webView);
        ApplyCurrentWebHostSize(webView);
        Panel.SetZIndex(webView, 1);
        ParkWebViewInViewport(webView);
        HidePlaceholder();
        _displayedTab = tab;

        if (previousTab is not null && previousTab != tab)
        {
            MoveWebViewToBackground(previousTab);
        }

        QueueFiveChLiveReadabilityActivation(tab, isActive: true);
    }

    private void ShowBlankView()
    {
        HideSplitViewForSingleTab(null);

        if (_displayedTab is not null)
        {
            MoveWebViewToBackground(_displayedTab);
        }

        ShowPlaceholder(_blankView);
        _displayedTab = null;
    }

    private bool CanStartBlankViewGesture()
    {
        return ActiveTab is { IsNativeContentTab: false } tab
            && IsBlankUrl(tab.Url)
            && ReferenceEquals(PlaceholderHost.Content, _blankView)
            && PlaceholderHost.Visibility == Visibility.Visible;
    }

    private long? TryBeginBlankViewGesture()
    {
        return CanStartBlankViewGesture() && ActiveTab is { } tab
            ? TryBeginWorkspaceGesture(tab.Id)
            : null;
    }

    private long? TryBeginBlankViewRightWheel()
    {
        return CanStartBlankViewGesture() && ActiveTab is { } tab
            ? TryBeginWorkspaceRightWheel(tab.Id)
            : null;
    }

    private void MoveWebViewToBackground(BrowserTab tab)
    {
        if (GetTabWebView(tab) is not { } webView)
        {
            return;
        }

        EnsureWebViewHosted(webView);
        ApplyCurrentWebHostSize(webView);
        Panel.SetZIndex(webView, 0);
        ParkWebViewOutsideViewport(webView);
        QueueFiveChLiveReadabilityActivation(tab, isActive: false);
    }

    private void ShowPlaceholder(UIElement content)
    {
        HideWorkspaceTab();
        HideSplitViewForSingleTab(null);
        HideAllFolderTabContents();
        if (!ReferenceEquals(PlaceholderHost.Content, content))
        {
            DetachHostedContent(content);
            PlaceholderHost.Content = content;
        }

        PlaceholderHost.Visibility = Visibility.Visible;
        Panel.SetZIndex(PlaceholderHost, 2);
    }

    private void HidePlaceholder()
    {
        HideWorkspaceTab();
        PlaceholderHost.Visibility = Visibility.Collapsed;
    }

    private void HideWorkspaceTab()
    {
        if (WorkspaceHost.Content is Views.WorkspaceOSView workspaceView)
        {
            workspaceView.EndInteractiveResizePreview();
            workspaceView.SetActive(false);
        }

        WorkspaceHost.Visibility = Visibility.Hidden;
    }

    private void EnsureWebViewHosted(WebView2 webView)
    {
        EnsureWebViewHostedIn(webView, WebHost);
    }

    private static bool EnsureWebViewHostedIn(WebView2 webView, Panel host)
    {
        webView.HorizontalAlignment = HorizontalAlignment.Left;
        webView.VerticalAlignment = VerticalAlignment.Top;

        if (host.Children.Contains(webView))
        {
            return true;
        }

        if (webView.Parent is Panel parent)
        {
            parent.Children.Remove(webView);
        }
        else if (webView.Parent is not null)
        {
            return false;
        }

        var visualParent = VisualTreeHelper.GetParent(webView);
        if (visualParent is Panel visualParentPanel && visualParentPanel != host)
        {
            visualParentPanel.Children.Remove(webView);
        }

        if (host.Children.Contains(webView))
        {
            return true;
        }

        if (webView.Parent is not null || VisualTreeHelper.GetParent(webView) is not null)
        {
            return false;
        }

        ParkWebViewOutsideViewport(webView);
        Panel.SetZIndex(webView, 0);
        try
        {
            host.Children.Add(webView);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static void ParkWebViewInViewport(WebView2 webView)
    {
        webView.Margin = new Thickness(0);
    }

    private static void ParkWebViewOutsideViewport(WebView2 webView)
    {
        webView.Margin = new Thickness(ParkedWebViewOffset, 0, 0, 0);
    }

    private void UpdateHostedWebViewSizes()
    {
        if (_splitViewService.IsActive && SplitViewHost.Visibility == Visibility.Visible)
        {
            ApplySplitViewWebViewSizes();
            return;
        }

        if (GetTabWebView(_displayedTab) is { } displayedWebView)
        {
            ApplyCurrentWebHostSize(displayedWebView);
        }
    }

    private void ApplyCurrentWebHostSize(WebView2 webView)
    {
        var size = GetCurrentWebHostSize();
        if (Math.Abs(webView.Width - size.Width) < 0.5
            && Math.Abs(webView.Height - size.Height) < 0.5)
        {
            return;
        }

        webView.Width = size.Width;
        webView.Height = size.Height;
    }

    private void PlaceWebViewForSplitPreparation(BrowserTab tab)
        => _splitViewDisplayController.PlaceWebViewForPreparation(tab);

    private void AppendSplitViewDiagnostic(
        string operation,
        BrowserTab? leftTab = null,
        BrowserTab? rightTab = null,
        BrowserTab? tab = null,
        string? pane = null,
        string? detail = null)
    {
        if (_isPrivateWindow || IsRightWheelTabCyclingActive())
        {
            return;
        }

        var fields = new List<string>
        {
            "runner=split",
            $"operation={NormalizeSessionDiagnosticToken(operation)}",
            $"window={_workspaceWindowIndex}",
            $"preparing={_splitViewWindowStateCoordinator.IsPreparing}",
            $"active={_splitViewService.IsActive}",
            $"visible={SplitViewHost.Visibility}",
            $"activePane={NormalizeSessionDiagnosticToken(_splitViewService.ActiveSplitView?.ActivePane ?? string.Empty)}",
            $"orientation={NormalizeSessionDiagnosticToken(_splitViewService.ActiveSplitView?.Orientation ?? string.Empty)}",
            $"webHost={FormatDiagnosticSize(WebHost.ActualWidth, WebHost.ActualHeight)}",
            $"leftHost={FormatDiagnosticSize(SplitLeftPaneHost.ActualWidth, SplitLeftPaneHost.ActualHeight)}",
            $"rightHost={FormatDiagnosticSize(SplitRightPaneHost.ActualWidth, SplitRightPaneHost.ActualHeight)}"
        };

        if (!string.IsNullOrWhiteSpace(pane))
        {
            fields.Add($"pane={NormalizeSessionDiagnosticToken(pane)}");
        }

        if (leftTab is not null)
        {
            fields.Add(BuildSplitTabDiagnostic("left", leftTab));
        }

        if (rightTab is not null)
        {
            fields.Add(BuildSplitTabDiagnostic("right", rightTab));
        }

        if (tab is not null)
        {
            fields.Add(BuildSplitTabDiagnostic("tab", tab));
        }

        if (!string.IsNullOrWhiteSpace(detail))
        {
            fields.Add($"detail={NormalizeSessionDiagnosticToken(detail)}");
        }

        AppendDiagnosticLogLine(fields.ToArray());
    }

    private string BuildSplitTabDiagnostic(string label, BrowserTab tab)
    {
        var webView = GetTabWebView(tab);
        var source = GetWebViewSourceForDiagnostic(webView);
        var url = string.IsNullOrWhiteSpace(tab.Url) ? string.Empty : SummarizeDiagnosticUrl(tab.Url);
        return NormalizeSessionDiagnosticToken(string.Join(
            ';',
            $"{label}Id={tab.Id}",
            $"url={url}",
            $"source={source}",
            $"loaded={tab.IsLoaded}",
            $"loading={tab.IsLoading}",
            $"initializing={_webViewLifecycleService.IsInitializing(tab)}",
            $"suspended={tab.IsSuspended}",
            $"parent={GetWebViewParentDiagnosticName(webView)}",
            $"webView={FormatDiagnosticSize(webView?.Width ?? 0, webView?.Height ?? 0)}",
            $"visibility={webView?.Visibility.ToString() ?? "null"}",
            $"z={GetWebViewZIndexForDiagnostic(webView)}"));
    }

    private string GetWebViewParentDiagnosticName(WebView2? webView)
    {
        return webView?.Parent switch
        {
            null => "null",
            var parent when ReferenceEquals(parent, WebHost) => "WebHost",
            var parent when ReferenceEquals(parent, SplitLeftPaneHost) => "SplitLeftPaneHost",
            var parent when ReferenceEquals(parent, SplitRightPaneHost) => "SplitRightPaneHost",
            var parent => parent.GetType().Name
        };
    }

    private static string GetWebViewSourceForDiagnostic(WebView2? webView)
    {
        try
        {
            var source = webView?.CoreWebView2?.Source;
            return string.IsNullOrWhiteSpace(source)
                ? string.Empty
                : SummarizeDiagnosticUrl(source);
        }
        catch (Exception ex)
        {
            return "error:" + ex.GetType().Name;
        }
    }

    private static int GetWebViewZIndexForDiagnostic(WebView2? webView)
    {
        return webView is null ? 0 : Panel.GetZIndex(webView);
    }

    private static string FormatDiagnosticSize(double width, double height)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Math.Round(width, 1)}x{Math.Round(height, 1)}");
    }

    private async Task StartSplitViewWithRightTabAsync(
        BrowserTab rightTab,
        BrowserTab? leftTabOverride = null,
        string orientation = SplitViewOrientation.LeftRight)
    {
        if ((leftTabOverride ?? ActiveTab) is not { } leftTab)
        {
            SetStatus("分割表示する現在タブがありません", autoClear: true);
            return;
        }

        await StartSplitViewAsync(leftTab, rightTab, orientation);
    }

    private async Task StartSplitViewAsync(
        BrowserTab leftTab,
        BrowserTab rightTab,
        string orientation = SplitViewOrientation.LeftRight,
        CancellationToken cancellationToken = default)
        => await _splitViewDisplayController.StartAsync(leftTab, rightTab, orientation, cancellationToken);

    private async Task<bool> EnsureSplitViewTabsLoadedAsync(CancellationToken cancellationToken = default)
        => await _splitViewDisplayController.EnsureTabsLoadedAsync(cancellationToken);

    private bool ShowActiveSplitView()
        => _splitViewDisplayController.ShowActive();

    private void RefreshActiveSplitViewWebViewPlacement(bool kickBlankNavigation = false)
        => _splitViewDisplayController.RefreshPlacement(kickBlankNavigation);

    private static bool IsSplitPaneContentReady(BrowserTab? tab)
    {
        return tab is not null && (tab.IsFolderTab ? IsReadyFolderTab(tab) : GetTabWebView(tab) is not null);
    }

    private void DetachHostedContent(UIElement content)
    {
        if (ReferenceEquals(PlaceholderHost.Content, content))
        {
            PlaceholderHost.Content = null;
        }
        if (ReferenceEquals(WorkspaceHost.Content, content))
        {
            WorkspaceHost.Content = null;
        }
        var parent = (content as FrameworkElement)?.Parent;
        if (parent is Panel panel)
        {
            panel.Children.Remove(content);
        }
        else if (parent is ContentControl contentControl && ReferenceEquals(contentControl.Content, content))
        {
            contentControl.Content = null;
        }
    }

    private void QueueSplitViewDisplayRefresh()
        => _splitViewDisplayController.QueueDisplayRefresh();

    private void QueueSplitYouTubeControlPanelRefresh()
        => _youTubeControlPanelController.QueueSplitYouTubeControlPanelRefresh();

    private void HideSplitViewForSingleTab(BrowserTab? tabToKeepVisible)
        => _splitViewDisplayController.HideForSingleTab(tabToKeepVisible);

    private void ClearSplitViewForSingleTab(BrowserTab? tabToKeepVisible)
        => _splitViewDisplayController.ClearForSingleTab(tabToKeepVisible);

    private void DetachSplitView(BrowserTab? tabToShow)
        => _splitViewDisplayController.Detach(tabToShow);

    private IEnumerable<BrowserTab> GetActiveSplitTabs()
    {
        if (_splitViewService.ActiveSplitView is not { } splitView)
        {
            yield break;
        }

        foreach (var tab in _tabs)
        {
            if (tab.Id == splitView.LeftTabId || tab.Id == splitView.RightTabId)
            {
                yield return tab;
            }
        }
    }

    private void MoveTabContentToBackground(BrowserTab tab)
    {
        if (tab.IsFolderTab && GetTabHostedContent(tab) is UIElement content)
        {
            ParkFolderTabContent(content);
            return;
        }

        MoveWebViewToBackground(tab);
    }

    private void ShowSingleTabContent(BrowserTab tab)
    {
        if (tab.IsFolderTab)
        {
            ShowFolderTab(tab);
            return;
        }

        ShowWebView(tab);
    }

    private void ApplySplitViewRatio()
        => _splitViewDisplayController.ApplyRatio();

    private void RefreshSplitViewTabVisualStates()
        => _splitViewDisplayController.RefreshTabVisualStates();

    private void ApplySplitViewWebViewSizes()
        => _splitViewDisplayController.ApplyWebViewSizes();

    private bool IsTopBottomSplitView()
        => _splitViewDisplayController.IsTopBottom();

    private void ActivateSplitPane(string pane)
        => _splitViewDisplayController.ActivatePane(pane);

    private void ActivateSplitPaneForTab(BrowserTab tab)
    {
        if (_splitViewService.TryGetPane(tab, out var pane))
        {
            ActivateSplitPane(pane);
        }
    }

    private void PersistSplitViewState()
    {
        try
        {
            _splitViewStore.Save(_splitViewService.Snapshot());
        }
        catch (Exception ex)
        {
            AppendSessionDiagnostic("split-view-save-failed", ex);
        }
    }

    private static bool CanOpenInSplitView(BrowserTab? tab)
    {
        return tab is not null
            && !tab.IsWorkspaceTab
            && (tab.IsFolderTab
                ? IsReadyFolderTab(tab)
                : !string.IsNullOrWhiteSpace(tab.Url)
                    && !IsNewTabUrl(tab.Url)
                    && !IsBlankUrl(tab.Url)
                    && !IsGeneratedNewTabSource(tab.Url));
    }

    private static bool IsSplitViewLinkUrl(string? url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static bool CanUseContextPageUrl(string? url)
    {
        return IsSplitViewLinkUrl(url);
    }

    private void QueueTabLayoutUpdate()
        => _tabStripController?.QueueTabLayoutUpdate();

    private void RefreshTabLayoutAfterTabCollectionChanged()
        => _tabStripController?.RefreshTabLayoutAfterTabCollectionChanged();

    private Size GetCurrentWebHostSize()
    {
        var width = WebHost.ActualWidth;
        var height = WebHost.ActualHeight;

        if (!double.IsFinite(width) || width < 1)
        {
            width = BackgroundWebViewWidth;
        }

        if (!double.IsFinite(height) || height < 1)
        {
            height = BackgroundWebViewHeight;
        }

        return new Size(width, height);
    }

    private void RemoveWebViewFromHosts(BrowserTab tab)
    {
        List<Exception>? exceptions = null;
        void RunCleanup(Action cleanup)
        {
            try
            {
                cleanup();
            }
            catch (Exception ex)
            {
                (exceptions ??= []).Add(ex);
            }
        }

        RunCleanup(() => _webViewLifecycleService.InvalidatePendingOperations(tab));
        RunCleanup(() => DetachWebViewRuntimeEventHandlers(tab));

        var webView = GetTabWebView(tab);
        if (webView?.Parent is Panel parent && IsOwnedWebViewHost(parent))
        {
            RunCleanup(() => parent.Children.Remove(webView));
        }

        RunCleanup(() =>
        {
            if (_displayedTab == tab)
            {
                _displayedTab = null;
            }
        });

        if (exceptions is { Count: 1 })
        {
            throw exceptions[0];
        }

        if (exceptions is { Count: > 1 })
        {
            throw new AggregateException("WebView2をWindowの表示ホストから安全に取り外せませんでした。", exceptions);
        }
    }

    private bool IsOwnedWebViewHost(Panel host)
    {
        return ReferenceEquals(host, WebHost)
            || ReferenceEquals(host, SplitLeftPaneHost)
            || ReferenceEquals(host, SplitRightPaneHost);
    }

    private Task CaptureTabThumbnailAsync(BrowserTab tab, bool isBackgroundWarmup = false)
        => _tabThumbnailCaptureController.CaptureAsync(tab, isBackgroundWarmup);

    CancellationToken ITabThumbnailCaptureHost.LifetimeToken => _windowTaskSupervisor.LifetimeToken;
    bool ITabThumbnailCaptureHost.ContainsTab(BrowserTab tab) => _tabs.Contains(tab);
    bool ITabThumbnailCaptureHost.IsActiveTab(BrowserTab tab) => ActiveTab == tab;
    void ITabThumbnailCaptureHost.ClearLoadFailure(BrowserTab tab) => ClearWebViewLoadFailure(tab);
    Task ITabThumbnailCaptureHost.EnsureLoadedAsync(BrowserTab tab, string url) => EnsureTabLoadedAsync(tab, url);
    bool ITabThumbnailCaptureHost.ResumeIfSuspended(BrowserTab tab) => ResumeTabIfSuspended(tab);
    bool ITabThumbnailCaptureHost.IsInitializing(BrowserTab tab) => _webViewLifecycleService.IsInitializing(tab);
    Task ITabThumbnailCaptureHost.WaitForInitializationAsync(BrowserTab tab) => _webViewLifecycleService.WaitForInitializationAsync(tab);
    Func<string, string?, CancellationToken, Task<WebThumbnailCaptureResult>>? ITabThumbnailCaptureHost.GetCaptureAction(BrowserTab tab)
        => GetTabWebView(tab)?.CoreWebView2 is { } core
            ? (title, url, token) => WebThumbnailCaptureService.CaptureWithSummaryAsync(core, title, url, token)
            : null;
    ImageSource ITabThumbnailCaptureHost.CreateFallbackThumbnail(BrowserTab tab) => CreateFallbackThumbnail(tab);
    void ITabThumbnailCaptureHost.SetThumbnail(BrowserTab tab, ImageSource image) => _ = s_tabRuntimeCoordinator.SetThumbnail(tab, image);
    Task ITabThumbnailCaptureHost.TrySuspendAsync(BrowserTab tab) => TrySuspendTabAsync(tab);
    void ITabThumbnailCaptureHost.UpdateRecentThumbnail(BrowserTab tab) => UpdateRecentTabThumbnail(tab);
    void ITabThumbnailCaptureHost.RecordCaptureDiagnostic(BrowserTab tab, string? url, long elapsedMilliseconds, string detail, bool isBackgroundWarmup)
        => AppendWebViewPerformanceDiagnostic("webview-thumbnail-capture", tab, url, elapsedMilliseconds, detail, thumbnailWarmup: isBackgroundWarmup);

    private static ImageSource CreateFallbackThumbnail(BrowserTab tab)
    {
        return CreateFallbackThumbnail(tab.DisplayTitle, tab.Url);
    }

    private static ImageSource CreateFallbackThumbnail(string titleText, string? urlText)
        => s_previewThumbnailRenderer.CreateFallbackThumbnail(titleText, urlText);

    private void UpdateTabLayout()
        => _tabStripController?.UpdateTabLayout();

    bool ITopToolbarHost.IsAutoHideEnabled => IsTopToolbarAutoHideEnabled();
    bool ITopToolbarHost.IsApplicationFullscreenActive => _youTubeControlWorkCoordinator.IsApplicationFullscreenActive;
    bool ITopToolbarHost.HasOpenSurface
        => IsCommandPaletteOpen || IsAnyTopToolbarPopupOpen() || IsAnyTopToolbarContextMenuOpen();

    private void RootGrid_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!IsTopToolbarAutoHideEnabled())
        {
            return;
        }

        _topToolbarController?.HandlePointerMoved(e.GetPosition(RootGrid));
    }

    private void RootGrid_MouseLeave(object sender, MouseEventArgs e)
        => QueueTopToolbarAutoHide();

    private void TopToolbar_MouseEnter(object sender, MouseEventArgs e)
        => ShowTopToolbar();

    private void TopToolbar_MouseLeave(object sender, MouseEventArgs e)
        => QueueTopToolbarAutoHide();

    private void TopToolbarScrollViewport_SizeChanged(object sender, SizeChangedEventArgs e)
        => _topToolbarController?.QueueOverflowUpdate(DispatcherPriority.Render);

    private void TopToolbarGrid_Loaded(object sender, RoutedEventArgs e)
        => _topToolbarController?.QueueOverflowUpdate(DispatcherPriority.ContextIdle);

    private void TopToolbarScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        => _topToolbarController?.QueueOverflowUpdate(DispatcherPriority.Render);

    private void TopToolbarScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_topToolbarController?.HandleMouseWheel(e.Delta) == true)
        {
            e.Handled = true;
        }
    }

    private void TopToolbarScrollLeftButton_Click(object sender, RoutedEventArgs e)
        => _topToolbarController.ScrollTopToolbarBy(-TopToolbarController.TopToolbarScrollStep);

    private void TopToolbarScrollRightButton_Click(object sender, RoutedEventArgs e)
        => _topToolbarController.ScrollTopToolbarBy(TopToolbarController.TopToolbarScrollStep);

    private void UpdateTopToolbarOverflowState()
        => _topToolbarController?.UpdateTopToolbarOverflowState();

    private void QueueTopToolbarLayoutAfterPageZoomVisibilityChange()
        => _topToolbarController?.QueueTopToolbarLayoutAfterPageZoomVisibilityChange();

    private void TopToolbarContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        ShowTopToolbar();
        AutoHideToolbarMenuItem.IsChecked = IsTopToolbarAutoHideEnabled();
    }

    private void AutoHideToolbarMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _settings.ToolbarVisibility ??= new ToolbarVisibilitySettings();
        _settings.ToolbarVisibility.AutoHideToolbar = AutoHideToolbarMenuItem.IsChecked;
        SettingsService.Save(_settings);
        ApplyTopToolbarAutoHideState();
        SetStatus(
            _settings.ToolbarVisibility.AutoHideToolbar
                ? "上部ツールバーの自動開閉を有効にしました"
                : "上部ツールバーの自動開閉を無効にしました",
            autoClear: true);
    }

    private bool IsTopToolbarAutoHideEnabled()
    {
        return _settings.ToolbarVisibility?.AutoHideToolbar == true;
    }

    private void ApplyTopToolbarAutoHideState()
        => _topToolbarController.ApplyTopToolbarAutoHideState();

    private void ShowTopToolbar()
        => _topToolbarController?.ShowTopToolbar();

    private void QueueTopToolbarAutoHide()
        => _topToolbarController?.QueueTopToolbarAutoHide();

    private bool IsAnyTopToolbarPopupOpen()
    {
        return ClosedTabHistoryPopup.IsOpen
            || ChatGptReadabilityPopup.IsOpen
            || DownloadShelfPopup.IsOpen
            || AddressSuggestionPopup.IsOpen

            || PageZoomPopup.IsOpen
            || _extensionPopup?.IsOpen == true;
    }

    private bool IsAnyTopToolbarContextMenuOpen()
    {
        return AddressBox.ContextMenu?.IsOpen == true
            || ActionsButton.ContextMenu?.IsOpen == true
            || ToolsButton.ContextMenu?.IsOpen == true
            || TopToolbar.ContextMenu?.IsOpen == true
            || WindowCloseButton.ContextMenu?.IsOpen == true
            || TitleBarRightDragArea.ContextMenu?.IsOpen == true;
    }

    private void ApplyToolbarVisibility()
    {
        var toolbarVisibility = _settings.ToolbarVisibility ??= new ToolbarVisibilitySettings();
        ApplyTopToolbarAutoHideState();
        BackButton.Visibility = ToVisibility(toolbarVisibility.ShowBackButton);
        ForwardButton.Visibility = ToVisibility(toolbarVisibility.ShowForwardButton);
        ReloadButton.Visibility = ToVisibility(toolbarVisibility.ShowReloadButton);
        RestoreButton.Visibility = ToVisibility(toolbarVisibility.ShowRestoreButton);
        ClosedTabHistoryButton.Visibility = ToVisibility(toolbarVisibility.ShowClosedTabHistoryButton);
        AddressArea.Visibility = ToVisibility(toolbarVisibility.ShowAddressBox);
        GoButton.Visibility = ToVisibility(toolbarVisibility.ShowGoButton);
        ActionsButton.Visibility = ToVisibility(toolbarVisibility.ShowActionsButton);
        AddBookmarkButton.Visibility = ToVisibility(toolbarVisibility.ShowAddBookmarkButton);
        BookmarkListButton.Visibility = ToVisibility(toolbarVisibility.ShowBookmarkListButton);
        WorkspaceOSButton.Visibility = ToVisibility(toolbarVisibility.ShowWorkspaceOSButton);

        ToolsButton.Visibility = ToVisibility(toolbarVisibility.ShowToolsButton);
        AddExtensionButton.Visibility = ToVisibility(toolbarVisibility.ShowAddExtensionButton);
        ImportExtensionButton.Visibility = ToVisibility(toolbarVisibility.ShowImportExtensionButton);
        ManageExtensionsButton.Visibility = ToVisibility(toolbarVisibility.ShowManageExtensionsButton);
        GestureSettingsButton.Visibility = ToVisibility(toolbarVisibility.ShowGestureSettingsButton);
        SettingsButton.Visibility = ToVisibility(toolbarVisibility.ShowSettingsButton);
        ExtensionIconList.Visibility = ToVisibility(toolbarVisibility.ShowExtensionIcons);
        _statusPresentationController.RefreshVisibility();

        if (!toolbarVisibility.ShowClosedTabHistoryButton)
        {
            ClosedTabHistoryPopup.IsOpen = false;
        }

        if (!toolbarVisibility.ShowActionsButton && ActionsButton.ContextMenu is { } actionsMenu)
        {
            actionsMenu.IsOpen = false;
        }

        if (!toolbarVisibility.ShowToolsButton && ToolsButton.ContextMenu is { } toolsMenu)
        {
            toolsMenu.IsOpen = false;
        }

        UpdateToolbarState();
        _topToolbarController.QueueOverflowUpdate(DispatcherPriority.Loaded);
    }

    private void SyncXReadableModeMenuItem()
    {
        XReadableModeMenuItem.IsChecked = _settings.EnableXReadableMode;
    }

    private static Visibility ToVisibility(bool isVisible)
    {
        return isVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private static ToolbarVisibilitySettings CloneToolbarVisibility(ToolbarVisibilitySettings? settings)
    {
        settings ??= new ToolbarVisibilitySettings();
        return new ToolbarVisibilitySettings
        {
            ShowBackButton = settings.ShowBackButton,
            AutoHideToolbar = settings.AutoHideToolbar,
            ShowForwardButton = settings.ShowForwardButton,
            ShowReloadButton = settings.ShowReloadButton,
            ShowRestoreButton = settings.ShowRestoreButton,
            ShowClosedTabHistoryButton = settings.ShowClosedTabHistoryButton,
            ShowAddressBox = settings.ShowAddressBox,
            ShowGoButton = settings.ShowGoButton,
            ShowActionsButton = settings.ShowActionsButton,
            ShowAddBookmarkButton = settings.ShowAddBookmarkButton,
            ShowBookmarkListButton = settings.ShowBookmarkListButton,
            ShowWorkspaceOSButton = settings.ShowWorkspaceOSButton,

            PreservedCompatibilitySettings = PublicSettingsCompatibility.Clone(settings.PreservedCompatibilitySettings, toolbar: true),

            ShowToolsButton = settings.ShowToolsButton,
            ShowAddExtensionButton = settings.ShowAddExtensionButton,
            ShowImportExtensionButton = settings.ShowImportExtensionButton,
            ShowManageExtensionsButton = settings.ShowManageExtensionsButton,
            ShowGestureSettingsButton = settings.ShowGestureSettingsButton,
            ShowSettingsButton = settings.ShowSettingsButton,
            ShowExtensionIcons = settings.ShowExtensionIcons,
            ShowStatusText = settings.ShowStatusText
        };
    }

    private void UpdateToolbarState()
    {
        var tab = GetActiveBrowserCommandTarget();
        BackButton.IsEnabled = CanNavigateBrowserHistory(tab, offset: -1);
        ForwardButton.IsEnabled = CanNavigateBrowserHistory(tab, offset: 1);
        ReloadButton.IsEnabled = tab is not null;
        RestoreButton.IsEnabled = s_closedTabWindowHistoryStore.ClosedTabs.Count > 0;
        ClosedTabHistoryButton.IsEnabled =
            s_closedTabWindowHistoryStore.ClosedTabs.Count > 0 || HasSupplementalClosedItemHistory();
        var canControlYouTube = tab is { IsNativeContentTab: false } activeTab
            && GetTabWebView(activeTab)?.CoreWebView2 is not null
            && GetCurrentPageUrl(activeTab) is { } url
            && IsYouTubeVideoUrl(url);
        UpdateYouTubeControlPanelState(canControlYouTube);
        UpdatePageToolsForActiveTarget();
        UpdateSiteInformationButtonState();

        var isChatGptTab = IsActiveChatGptTab();
        ChatGptReadabilityButton.Visibility = isChatGptTab ? Visibility.Visible : Visibility.Collapsed;
        ChatGptReadabilityButton.IsEnabled = isChatGptTab;
        if (!isChatGptTab)
        {
            ChatGptReadabilityPopup.IsOpen = false;
        }

        UpdateAmazonPrimeVideoNotice();
    }

    private void ApplyWindowPlacement(WindowPlacementSettings? placement)
    {
        if (placement is null
            || !IsValidWindowLength(placement.Width)
            || !IsValidWindowLength(placement.Height)
            || !double.IsFinite(placement.Left)
            || !double.IsFinite(placement.Top))
        {
            return;
        }

        var width = Math.Max(MinWidth, placement.Width);
        var height = Math.Max(MinHeight, placement.Height);
        var left = placement.Left;
        var top = placement.Top;

        if (!IntersectsVirtualScreen(left, top, width, height))
        {
            left = SystemParameters.WorkArea.Left + 40;
            top = SystemParameters.WorkArea.Top + 40;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left;
        Top = top;
        Width = width;
        Height = height;

        if (placement.State.Equals("Maximized", StringComparison.OrdinalIgnoreCase))
        {
            WindowState = WindowState.Maximized;
        }

        UpdateWindowBottomResizeStrip();

        if (IsLoaded)
        {
            WindowPlacementVisibilityService.EnsureVisibleOnNearestWorkArea(this);
        }
    }

    private void ApplyInitialWindowPosition(Point position)
    {
        WindowStartupLocation = WindowStartupLocation.Manual;

        var width = Math.Max(MinWidth, Width);
        var height = Math.Max(MinHeight, Height);
        var left = position.X - 80;
        var top = position.Y - 24;

        Left = ClampToVirtualScreen(left, width, SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenWidth);
        Top = ClampToVirtualScreen(top, height, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenHeight);
        Width = width;
        Height = height;
    }

    private void CaptureWindowPlacement()
    {
        if (CreateWindowPlacement() is { } placement)
        {
            _settings.WindowPlacement = placement;
        }
    }

    private WindowPlacementSettings? CreateWindowPlacement()
    {
        var placementState = WindowState == WindowState.Maximized ? "Maximized" : "Normal";
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;

        if (_youTubeControlWorkCoordinator.TryGetApplicationFullscreenRestoreSnapshot(
            out var fullscreenRestoreSnapshot))
        {
            bounds = new Rect(
                fullscreenRestoreSnapshot.Left,
                fullscreenRestoreSnapshot.Top,
                fullscreenRestoreSnapshot.Width,
                fullscreenRestoreSnapshot.Height);
            placementState = (WindowState)fullscreenRestoreSnapshot.WindowState == WindowState.Maximized
                ? "Maximized"
                : "Normal";
        }

        if (!IsValidWindowLength(bounds.Width)
            || !IsValidWindowLength(bounds.Height)
            || !double.IsFinite(bounds.Left)
            || !double.IsFinite(bounds.Top))
        {
            return null;
        }

        return new WindowPlacementSettings
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = Math.Max(MinWidth, bounds.Width),
            Height = Math.Max(MinHeight, bounds.Height),
            State = placementState
        };
    }

    private static bool IntersectsVirtualScreen(double left, double top, double width, double height)
    {
        var right = left + width;
        var bottom = top + height;
        var screenLeft = SystemParameters.VirtualScreenLeft;
        var screenTop = SystemParameters.VirtualScreenTop;
        var screenRight = screenLeft + SystemParameters.VirtualScreenWidth;
        var screenBottom = screenTop + SystemParameters.VirtualScreenHeight;

        return right > screenLeft
            && left < screenRight
            && bottom > screenTop
            && top < screenBottom;
    }

    private static bool IsValidWindowLength(double value)
    {
        return double.IsFinite(value) && value > 0;
    }

    private static double ClampToVirtualScreen(double value, double length, double screenStart, double screenLength)
    {
        if (!double.IsFinite(value) || !double.IsFinite(length) || length <= 0)
        {
            return screenStart;
        }

        var max = screenStart + Math.Max(0, screenLength - length);
        return Math.Clamp(value, screenStart, max);
    }

    private Point GetCursorScreenPoint()
    {
        if (!TryGetCursorDevicePoint(out var devicePoint))
        {
            return PointToScreen(Mouse.GetPosition(this));
        }

        var source = PresentationSource.FromVisual(this);
        return source?.CompositionTarget?.TransformFromDevice.Transform(devicePoint)
            ?? devicePoint;
    }

    private static bool TryGetCursorDevicePoint(out Point point)
    {
        if (!GetCursorPos(out var cursor))
        {
            point = default;
            return false;
        }

        point = new Point(cursor.X, cursor.Y);
        return true;
    }

    private void QueueSessionSave()
    {
        if (!_isInitialized || _isPrivateWindow)
        {
            return;
        }

        s_sessionPersistenceCoordinator.RequestSave();
    }

    private void QueueWorkspaceOpenTabsRefresh()
    {
        if (!_isInitialized || _isClosingWindow)
        {
            return;
        }

        _workspaceOpenTabsRefreshTimer.Stop();
        _workspaceOpenTabsRefreshTimer.Start();
    }

    private void RefreshLoadedWorkspaceOpenTabs()
    {
        foreach (var window in Application.Current.Windows.OfType<MainWindow>().ToList())
        {
            window.RefreshLocalWorkspaceOpenTabs();
        }
    }

    private void RefreshLocalWorkspaceOpenTabs()
    {
        if (_isClosingWindow)
        {
            return;
        }

        foreach (var tab in _tabs)
        {
            if (GetTabHostedContent(tab) is Views.WorkspaceOSView workspaceView)
            {
                workspaceView.RefreshOpenTabs();
            }
        }

        _workspaceOSWindow?.RefreshOpenTabs();
        foreach (var window in _workspaceFolderWindows.Values.ToList())
        {
            window.RefreshOpenTabs();
        }
    }

    private void QueueWorkspaceOpenTabsRefreshForOtherWindows()
    {
        foreach (var window in Application.Current.Windows
            .OfType<MainWindow>()
            .Where(window => !ReferenceEquals(window, this))
            .ToList())
        {
            window.QueueWorkspaceOpenTabsRefresh();
        }
    }

    private static async Task CaptureYouTubePlaybackStateForSessionAsync(IReadOnlyList<MainWindow> windows)
    {
        foreach (var window in windows)
        {
            try
            {
                await window.CaptureYouTubePlaybackStateForLoadedTabsAsync(
                    queueSaveOnChange: false,
                    activeOnly: false);
            }
            catch (Exception ex)
            {
                AppendSessionDiagnostic("youtube-state-capture-failed", ex, $"window={window._workspaceWindowIndex}");
            }
        }
    }

    private static void AppendSessionSaveDiagnostic(Exception exception)
    {
        AppendSessionDiagnostic("save-failed", exception);
    }

    private static string GetSessionDiagnosticDirectory()
    {
        return Path.Combine(AppPaths.Root, "session");
    }

    private static string GetSessionDiagnosticLogPath()
    {
        return Path.Combine(GetSessionDiagnosticDirectory(), "diagnostic.log");
    }

    private static void AppendSessionDiagnostic(string operation, Exception exception, string? detail = null)
    {
        AppendDiagnosticLogLine(
            "runner=session",
            $"operation={NormalizeSessionDiagnosticToken(operation)}",
            $"type={NormalizeSessionDiagnosticToken(exception.GetType().Name)}",
            $"message={NormalizeSessionDiagnosticToken(exception.Message)}",
            string.IsNullOrWhiteSpace(detail)
                ? string.Empty
                : $"detail={NormalizeSessionDiagnosticToken(detail)}");
    }

    private void AppendWebViewPerformanceDiagnostic(
        string operation,
        BrowserTab? tab = null,
        string? url = null,
        long? elapsedMilliseconds = null,
        string? detail = null,
        bool thumbnailWarmup = false)
    {
        if (_isPrivateWindow)
        {
            return;
        }

        var fields = new List<string>
        {
            "runner=perf",
            $"operation={NormalizeSessionDiagnosticToken(operation)}",
            $"window={_workspaceWindowIndex}",
            $"backgroundLoad={_backgroundTabWorkCoordinator.IsTabLoading}",
            $"thumbnailWarmup={thumbnailWarmup}"
        };

        if (elapsedMilliseconds.HasValue)
        {
            fields.Add($"elapsedMs={elapsedMilliseconds.Value}");
        }

        if (tab is not null)
        {
            fields.Add($"tab={tab.Id}");
            fields.Add($"active={ActiveTab?.Id == tab.Id}");
            fields.Add($"loaded={tab.IsLoaded}");
            fields.Add($"loading={tab.IsLoading}");
            fields.Add($"initializing={_webViewLifecycleService.IsInitializing(tab)}");
            fields.Add($"suspended={tab.IsSuspended}");
        }

        if (!string.IsNullOrWhiteSpace(url))
        {
            fields.Add($"url={NormalizeSessionDiagnosticToken(SummarizeDiagnosticUrl(url))}");
        }

        if (!string.IsNullOrWhiteSpace(detail))
        {
            fields.Add($"detail={NormalizeSessionDiagnosticToken(detail)}");
        }

        QueuePerformanceDiagnosticLogLine(fields);
    }

    private static void AppendDiagnosticLogLine(params string[] fields)
        => s_diagnosticLogWriter.AppendLines([DiagnosticLogWriter.BuildLine(fields)]);

    private static long GetElapsedMilliseconds(long startTimestamp)
    {
        return (long)Math.Round(Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
    }

    private static string SummarizeDiagnosticUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        if (uri.Scheme.Equals("tabbrowser", StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals("about", StringComparison.OrdinalIgnoreCase))
        {
            return uri.ToString();
        }

        var path = uri.AbsolutePath;
        return string.IsNullOrWhiteSpace(path) || path == "/"
            ? $"{uri.Scheme}://{uri.Host}"
            : $"{uri.Scheme}://{uri.Host}{path}";
    }

    private static string NormalizeSessionDiagnosticToken(string value)
    {
        var normalized = value
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('|', '/')
            .Trim();
        return normalized.Length <= 500
            ? normalized
            : normalized[..500];
    }

    private static SessionPersistenceWorkItem? CaptureLatestSessionPersistenceWorkItem()
    {
        return s_sessionRuntimeCaptureAdapter.CaptureLatestPersistenceWorkItem(
            CreateSessionRuntimeWindowSources());
    }

    private static Task<SessionPersistenceWorkItem?> CaptureLatestSessionPersistenceWorkItemAsync()
    {
        return SessionPersistenceDispatcherBridge.InvokeAsync(CaptureLatestSessionPersistenceWorkItem);
    }

    private static void HandleScheduledSessionSaveFailure(Exception exception)
    {
        AppendSessionSaveDiagnostic(exception);
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        var status = "セッション保存に失敗しました: " + exception.Message;
        void ShowFailureStatus()
        {
            s_sessionRuntimeCaptureAdapter.TryReportStatusToPersistenceOwner(
                CreateSessionRuntimeWindowSources(),
                status);
        }

        if (dispatcher.CheckAccess())
        {
            ShowFailureStatus();
            return;
        }

        _ = dispatcher.BeginInvoke(ShowFailureStatus);
    }

    private static SessionData CreateSessionData(SessionWindowState windowState)
        => SessionSnapshotService.FromWindow(windowState);

    private static SessionData CreateSessionData(IReadOnlyList<MainWindow> windows)
    {
        return s_sessionRuntimeCaptureAdapter.CreateSnapshot(
            CreateSessionRuntimeWindowSources(windows));
    }

    private static List<SessionRuntimeWindowSource> CreateSessionRuntimeWindowSources()
    {
        var windows = Application.Current?.Windows
            .OfType<MainWindow>()
            .OrderBy(static window => window._workspaceWindowIndex)
            .ToList() ?? [];
        return CreateSessionRuntimeWindowSources(windows);
    }

    private static List<SessionRuntimeWindowSource> CreateSessionRuntimeWindowSources(
        IReadOnlyList<MainWindow> windows)
    {
        return windows
            .OrderBy(static window => window._workspaceWindowIndex)
            .Select(static window => new SessionRuntimeWindowSource(
                window._workspaceWindowIndex,
                window.IsVisible,
                window._isInitialized,
                window._isPrivateWindow,
                window._isClosingWindow,
                window._persistSession,
                window._sessionService,
                window.CreateWindowSessionState,
                window.CreateFallbackWindowSessionState,
                status =>
                {
                    if (!window._isClosingWindow)
                    {
                        window.SetStatus(status, autoClear: true);
                    }
                }))
            .ToList();
    }

    private SessionWindowState CreateWindowSessionState()
        => SessionRuntimeCaptureAdapter.CaptureWindow(this);

    private SessionWindowState CreateFallbackWindowSessionState()
        => SessionRuntimeCaptureAdapter.CaptureFallbackWindow(this);

    IList<BrowserTab> ISessionWindowCaptureHost.Tabs => _tabs;
    BrowserTab? ISessionWindowCaptureHost.ActiveTab => ActiveTab;
    Guid ISessionWindowCaptureHost.SessionWindowId => _sessionWindowId;
    bool ISessionWindowCaptureHost.IsAlwaysOnTop => _isAlwaysOnTop;
    SessionTabState? ISessionWindowCaptureHost.CreateSessionTabState(BrowserTab tab, bool includeNavigationHistory)
        => CreateSessionTabState(tab, includeNavigationHistory);
    WindowPlacementSettings? ISessionWindowCaptureHost.CreateWindowPlacement() => CreateWindowPlacement();
    void ISessionWindowCaptureHost.RecordTabCaptureFailure(BrowserTab tab, Exception exception)
        => AppendSessionDiagnostic("tab-state-capture-failed", exception, $"window={_workspaceWindowIndex};tab={tab.Id}");

    private static SessionTabState? CreateSessionTabState(BrowserTab tab, bool includeNavigationHistory)
    {
        var url = tab.IsFolderTab
            ? tab.Url?.Trim()
            : NormalizeSessionTabUrl(tab.Url, tab.Title);
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var navigationHistory = new List<string>();
        var navigationHistoryIndex = -1;
        if (includeNavigationHistory)
        {
            navigationHistory = CreatePersistedNavigationHistory(tab, url, out navigationHistoryIndex);
        }

        var sessionState = BrowserTabStateMapper.ToSessionTabState(BrowserTabStateMapper.Capture(tab));
        sessionState.Id = tab.Id == Guid.Empty ? Guid.NewGuid() : tab.Id;
        sessionState.Url = url;
        sessionState.FolderTabViewMode = (GetTabHostedContent(tab) as Views.FolderTabView)?.ViewMode;
        sessionState.FolderTabSortMode = (GetTabHostedContent(tab) as Views.FolderTabView)?.SortMode;
        sessionState.FolderTabSortDirection = (GetTabHostedContent(tab) as Views.FolderTabView)?.SortDirection;
        sessionState.FolderTabState = includeNavigationHistory
            ? (GetTabHostedContent(tab) as Views.FolderTabView)?.CaptureSessionState()
            : null;
        sessionState.NavigationHistory = navigationHistory;
        sessionState.NavigationHistoryIndex = navigationHistoryIndex;
        return sessionState;
    }

    private static SessionData CreateInitialSession(IReadOnlyList<SessionTabState> tabs)
        => SessionSnapshotService.CreateInitial(tabs, s_tabNavigationHistoryService, NormalizeSessionTabUrl);

    private string BuildGestureScript(BrowserTab tab)
    {
        var startThreshold = _settings.GestureStartThreshold.ToString(CultureInfo.InvariantCulture);
        var directionThreshold = _settings.GestureDirectionThreshold.ToString(CultureInfo.InvariantCulture);
        var preserveContextMenu = _settings.PreserveContextMenuOnShortRightClick
            ? "true"
            : "false";
        var gestureMappingsJson = JsonSerializer.Serialize(
            GestureSettingsCatalog.ResolveMappingsForScope(
                _settings.GestureMappings,
                GestureCommandScope.Web));
        var webMessageTokenJson = JsonSerializer.Serialize(GetTabWebMessageToken(tab));

        return $$"""
            (() => {
                const __tabBrowserWebMessageToken = {{webMessageTokenJson}};
                const __tabBrowserGestureSettings = {
                    gestureStartThreshold: {{startThreshold}},
                    gestureDirectionThreshold: {{directionThreshold}},
                    preserveContextMenuOnShortRightClick: {{preserveContextMenu}}
                };
                const __tabBrowserGestureCommands = {{gestureMappingsJson}};
                {{_gestureScript}}
            })();
            """;
    }

    private static string BuildXReadableModeScript(bool isEnabled)
    {
        return XReadableScriptBuilder.Build(isEnabled);
    }

    private string BuildNewTabHtml(BrowserTab tab)
    {
        var bookmarkData = _bookmarks
            .OrderByDescending(static x => x.OpenCount)
            .ThenByDescending(static x => x.LastOpenedAt ?? x.CreatedAt)
            .Select((bookmark, index) => new
            {
                id = bookmark.Id.ToString(),
                title = string.IsNullOrWhiteSpace(bookmark.Title) ? bookmark.Url : bookmark.Title,
                url = bookmark.Url,
                host = TryGetHost(bookmark.Url) ?? string.Empty,
                folderPath = bookmark.FolderPath ?? string.Empty,
                rank = Math.Max(0, 10000 - index)
            })
            .ToList();

        var bookmarkDataJson = JsonSerializer.Serialize(bookmarkData);
        var webMessageTokenJson = JsonSerializer.Serialize(GetTabWebMessageToken(tab));

        return NewTabPageHtmlBuilder.Build(bookmarkDataJson, webMessageTokenJson);
    }

    private static async Task<string> LoadScriptAsync(string fileName)
    {
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "Scripts", fileName);
        return File.Exists(scriptPath)
            ? await File.ReadAllTextAsync(scriptPath)
            : string.Empty;
    }

    private static Border CreateBlankView()
    {
        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(15, 17, 21))
        };
    }

    private static Border CreateErrorView(string message)
    {
        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(15, 17, 21)),
            Child = new TextBlock
            {
                Text = message,
                Foreground = new SolidColorBrush(Color.FromRgb(243, 244, 246)),
                Margin = new Thickness(24),
                TextWrapping = TextWrapping.Wrap
            }
        };
    }

    private Border CreateWebViewRecoveryView(BrowserTab tab, string title, string detail)
    {
        var titleText = new TextBlock
        {
            Text = title,
            Foreground = new SolidColorBrush(Color.FromRgb(243, 244, 246)),
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        };
        var detailText = new TextBlock
        {
            Text = detail,
            Foreground = new SolidColorBrush(Color.FromRgb(209, 213, 219)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        };
        var reloadButton = new Button
        {
            Content = "再読み込み",
            Width = 120,
            Height = 34,
            Background = new SolidColorBrush(Color.FromRgb(31, 41, 55)),
            Foreground = new SolidColorBrush(Color.FromRgb(243, 244, 246)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(75, 85, 99)),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        reloadButton.Click += async (_, _) => await RecoverWebViewProcessFailureAsync(tab);
        var summaryButton = new Button
        {
            Content = "診断要約",
            Width = 120,
            Height = 34,
            Background = new SolidColorBrush(Color.FromRgb(31, 41, 55)),
            Foreground = new SolidColorBrush(Color.FromRgb(243, 244, 246)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(75, 85, 99)),
            Margin = new Thickness(8, 0, 0, 0)
        };
        summaryButton.Click += (_, _) => ShowSessionDiagnosticSummary();
        var diagnosticButton = new Button
        {
            Content = "診断ログ",
            Width = 120,
            Height = 34,
            Background = new SolidColorBrush(Color.FromRgb(31, 41, 55)),
            Foreground = new SolidColorBrush(Color.FromRgb(243, 244, 246)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(75, 85, 99)),
            Margin = new Thickness(8, 0, 0, 0)
        };
        diagnosticButton.Click += (_, _) => OpenSessionDiagnosticLog();

        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(15, 17, 21)),
            Child = new StackPanel
            {
                Margin = new Thickness(24),
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 560,
                Children =
                {
                    titleText,
                    detailText,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Children =
                        {
                            reloadButton,
                            summaryButton,
                            diagnosticButton
                        }
                    }
                }
            }
        };
    }

    private void QueueWebViewProcessAutoRecovery(BrowserTab tab)
        => _webViewRecoveryController.QueueWebViewProcessAutoRecovery(tab);

    private void QueueWebViewRenderProcessReload(
        BrowserTab tab,
        CoreWebView2 core,
        string failure,
        CoreWebView2ProcessFailedKind failureKind)
        => _webViewRecoveryController.QueueWebViewRenderProcessReload(
            tab,
            new WebViewRenderRecoveryTarget(
                () => ReferenceEquals(GetTabWebView(tab)?.CoreWebView2, core),
                () => core.Reload(),
                () => TryGetBrowserProcessId(core)),
            failure,
            failureKind);

    private void QueueWebViewGpuMediaRecovery(
        BrowserTab tab,
        CoreWebView2 core,
        string failedUrl,
        string failure,
        CoreWebView2ProcessFailedKind failureKind)
        => _webViewRecoveryController.QueueWebViewGpuMediaRecovery(
            tab,
            new WebViewMediaRecoveryTarget(
                () => ReferenceEquals(GetTabWebView(tab)?.CoreWebView2, core)
                    && string.Equals(
                        GetCurrentPageUrl(tab) ?? tab.Url,
                        failedUrl,
                        StringComparison.Ordinal),
                () => core.ExecuteScriptAsync(YouTubeControlScriptService.BuildPlaybackHealthProbeScript()),
                () => core.Reload(),
                () => TryGetBrowserProcessId(core)),
            failure,
            failureKind);

    private void WebViewEnvironmentService_BrowserProcessExited(
        object? sender,
        WebViewBrowserProcessExit notification)
    {
        s_webViewProcessRecoveryCoordinator.NotifyBrowserProcessExited(notification);
    }

    private static uint? TryGetBrowserProcessId(CoreWebView2 core)
    {
        try
        {
            return core.BrowserProcessId;
        }
        catch
        {
            return null;
        }
    }

    private void QueueWebViewProcessGroupRecovery(WebViewBrowserProcessExit notification)
        => _webViewRecoveryController.QueueWebViewProcessGroupRecovery(notification);

    private Task RecoverWebViewProcessFailureAsync(BrowserTab tab, bool isAutomatic = false)
        => _webViewRecoveryController.RecoverWebViewProcessFailureAsync(tab, isAutomatic);

    IList<BrowserTab> IWebViewRecoveryHost.Tabs => _tabs;
    BrowserTab? IWebViewRecoveryHost.ActiveTab => ActiveTab;
    bool IWebViewRecoveryHost.IsClosing => _isClosingWindow;
    bool IWebViewRecoveryHost.HasDispatcherShutdownStarted => Dispatcher.HasShutdownStarted;
    bool IWebViewRecoveryHost.HasWebView(BrowserTab tab) => GetTabWebView(tab) is not null;
    bool IWebViewRecoveryHost.IsWebViewVisible(BrowserTab tab) => GetTabWebView(tab)?.Visibility == Visibility.Visible;
    bool IWebViewRecoveryHost.IsFirstActiveWindow()
        => ReferenceEquals(this, GetOpenMainWindows().FirstOrDefault(static window => window.IsActive));
    bool IWebViewRecoveryHost.TryRunSupervised(string operation, Func<CancellationToken, Task> action)
        => _windowTaskSupervisor.TryRun(operation, action);
    Task IWebViewRecoveryHost.InvokeInBackgroundAsync(Func<Task> action)
        => Dispatcher.InvokeAsync(action, DispatcherPriority.Background).Task.Unwrap();
    void IWebViewRecoveryHost.DisposeDefaultProfileAuxiliaryWebViews()
    {
        CloseExtensionPopup(disposeWebView: true);
        CloseBookmarkPreview(clearNavigation: false, disposeWebView: true);
        DisposeCommandPaletteUrlThumbnailWebView();
    }
    Task IWebViewRecoveryHost.RecoverFloatingMediaProcessGroupAsync(string profileId, uint browserProcessId)
        => RecoverFloatingMediaProcessGroupAsync(profileId, browserProcessId);
    void IWebViewRecoveryHost.ReleaseTabRuntimeResources(BrowserTab tab) => ReleaseTabRuntimeResources(tab);
    Task IWebViewRecoveryHost.EnsureTabLoadedAsync(BrowserTab tab, string? url) => EnsureTabLoadedAsync(tab, url);
    void IWebViewRecoveryHost.MarkWebViewLoadFailure(BrowserTab tab, string message) => MarkWebViewLoadFailure(tab, message);
    void IWebViewRecoveryHost.ShowBlankView() => ShowBlankView();
    void IWebViewRecoveryHost.ShowWebViewRecoveryPlaceholder(BrowserTab tab, string title, string detail)
        => ShowWebViewRecoveryPlaceholder(tab, title, detail);
    void IWebViewRecoveryHost.SetStatus(string message, bool autoClear) => SetStatus(message, autoClear);
    void IWebViewRecoveryHost.AppendSessionDiagnostic(string operation, Exception exception, string detail)
        => AppendSessionDiagnostic(operation, exception, detail);
    void IWebViewRecoveryHost.QueueWorkspaceOpenTabsRefresh() => QueueWorkspaceOpenTabsRefresh();

    private void ShowSessionDiagnosticSummary()
    {
        var logPath = GetSessionDiagnosticLogPath();
        if (!File.Exists(logPath))
        {
            SetStatus("セッション診断ログはまだありません", autoClear: true);
            return;
        }

        try
        {
            var lines = File.ReadLines(logPath)
                .Where(static line => !string.IsNullOrWhiteSpace(line))
                .TakeLast(SessionDiagnosticSummaryFormatter.MaximumEntryCount)
                .ToList();
            if (lines.Count == 0)
            {
                SetStatus("セッション診断ログは空です", autoClear: true);
                return;
            }

            MessageBox.Show(
                this,
                SessionDiagnosticSummaryFormatter.Format(lines),
                "セッション診断",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            SetStatus("最新セッション診断を表示しました", autoClear: true);
        }
        catch (Exception ex)
        {
            SetStatus("セッション診断を表示できません: " + ex.Message, autoClear: true);
        }
    }

    private void OpenSessionDiagnosticLog()
    {
        var logPath = GetSessionDiagnosticLogPath();
        if (!File.Exists(logPath))
        {
            SetStatus("セッション診断ログはまだありません", autoClear: true);
            return;
        }

        if (TryOpenShellPath(logPath, out var errorMessage))
        {
            SetStatus("セッション診断ログを開きました", autoClear: true);
            return;
        }

        SetStatus("セッション診断ログを開けません: " + errorMessage, autoClear: true);
    }

    private void OpenSessionDiagnosticFolder()
    {
        try
        {
            Directory.CreateDirectory(GetSessionDiagnosticDirectory());
        }
        catch (Exception ex)
        {
            SetStatus("セッション診断フォルダーを準備できません: " + ex.Message, autoClear: true);
            return;
        }

        if (TryOpenShellPath(GetSessionDiagnosticDirectory(), out var errorMessage))
        {
            SetStatus("セッション診断フォルダーを開きました", autoClear: true);
            return;
        }

        SetStatus("セッション診断フォルダーを開けません: " + errorMessage, autoClear: true);
    }

    private static bool TryOpenShellPath(string path, out string errorMessage)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
            errorMessage = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private void SetStatus(string message, bool autoClear = false)
        => _statusPresentationController.SetStatus(message, autoClear);

    private void ClearStatus()
        => _statusPresentationController.ClearStatus();

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        => _statusPresentationController?.UpdatePopupPlacement();

    private void StatusPopup_Opened(object? sender, EventArgs e)
        => _statusPresentationController.HandlePopupOpened();

    bool IStatusPresentationHost.IsClosing => _isClosingWindow;

    bool IStatusPresentationHost.ShowStatusText
        => (_settings.ToolbarVisibility ??= new ToolbarVisibilitySettings()).ShowStatusText;

    bool IStatusPresentationHost.IsTabMoveUndoVisible
        => TabMoveUndoButton.Visibility == Visibility.Visible;

    void IStatusPresentationHost.ReapplyAlwaysOnTopIfNeeded()
        => ReapplyAlwaysOnTopIfNeeded();

    private static bool IsBlankUrl(string? url)
    {
        return string.IsNullOrWhiteSpace(url)
            || url.Equals("about:blank", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGeneratedNewTabSource(string? url)
    {
        return !string.IsNullOrWhiteSpace(url)
            && (url.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase)
                || url.Equals("about:blank", StringComparison.OrdinalIgnoreCase));
    }

    private static string? NormalizeSessionTabUrl(string? url, string? title)
    {
        var normalizedUrl = IsGeneratedNewTabSource(url)
            && string.Equals(title, "新しいタブ", StringComparison.Ordinal)
                ? NewTabUrl
                : url;
        return string.IsNullOrWhiteSpace(normalizedUrl)
            ? normalizedUrl
            : RemoveYouTubeNoAutoplayQuery(NormalizeYouTubeShortsUrl(normalizedUrl));
    }

    private static bool IsNewTabUrl(string? url)
    {
        return !string.IsNullOrWhiteSpace(url)
            && url.Equals(NewTabUrl, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUserGuideUrl(string? url)
    {
        return !string.IsNullOrWhiteSpace(url)
            && url.Equals(UserGuideUrl, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUserGuideContentUrl(string? url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && uri.Host.Equals(UserGuideVirtualHostName, StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.Equals("/user-guide.html", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWorkspaceTabUrl(string? url)
    {
        return !string.IsNullOrWhiteSpace(url)
            && url.Equals(WorkspaceTabUrl, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetTabDragPayload(DragEventArgs e, out TabDragPayload payload)
    {
        if (e.Data.GetDataPresent(typeof(TabDragPayload))
            && e.Data.GetData(typeof(TabDragPayload)) is TabDragPayload typedPayload)
        {
            payload = typedPayload;
            return true;
        }

        payload = default!;
        return false;
    }

    private void SetTabTransferData(DataObject data, BrowserTab tab)
    {
        if (!tab.IsNativeContentTab)
        {
            var snapshot = new TabTransferSnapshot
            {
                Url = string.IsNullOrWhiteSpace(tab.Url) ? NewTabUrl : tab.Url,
                Title = tab.Title,
                ContainerProfileId = tab.ContainerProfileId,
                IsPrivateWindow = _isPrivateWindow
            };

            data.SetData(TabDragTransferFormat, JsonSerializer.Serialize(snapshot));
        }

        var workspaceSnapshots = CreateWorkspaceDragSnapshots(tab);
        if (workspaceSnapshots.Count > 0)
        {
            data.SetData(
                TabShelfDragPayload.DataFormat,
                new TabShelfDragPayload(workspaceSnapshots, [], tab.Id));
        }
    }

    private IReadOnlyList<CapturedTabSnapshot> CreateWorkspaceDragSnapshots(BrowserTab tab)
    {
        var snapshots = _tabCaptureService.CaptureTabs(
                _tabs.ToList(),
                GetActiveBrowserTabIdForWorkspace(),
                _workspaceWindowId,
                GetWorkspaceWindowGroupTitle(this),
                _workspaceWindowIndex,
                isForeignWindow: false,
                _splitViewService.Snapshot());
        return WorkspaceTabSavePlanner.ExpandSplitPartners(
            snapshots.Where(snapshot => snapshot.TabId == tab.Id),
            snapshots);
    }

    private bool TrySaveDraggedTabToActiveWorkspace(BrowserTab tab, Point pointInRoot)
    {
        if (ActiveTab is not { IsWorkspaceTab: true } workspaceTab
            || GetTabHostedContent(workspaceTab) is not Views.WorkspaceOSView workspaceView)
        {
            return false;
        }

        var snapshots = CreateWorkspaceDragSnapshots(tab);
        if (snapshots.Count == 0)
        {
            return false;
        }

        var pointInWorkspace = RootGrid.TranslatePoint(pointInRoot, workspaceView);
        return workspaceView.TrySaveDroppedTabs(snapshots, pointInWorkspace);
    }

    private static bool TryGetTabTransferSnapshot(DragEventArgs e, out TabTransferSnapshot snapshot)
    {
        if (e.Data.GetDataPresent(TabDragTransferFormat)
            && e.Data.GetData(TabDragTransferFormat) is string json
            && TryDeserializeTabTransferSnapshot(json, out snapshot))
        {
            return true;
        }

        snapshot = default!;
        return false;
    }

    private static bool TryGetSiteInformationUrlDragSnapshot(
        DragEventArgs e,
        out SiteInformationUrlDragSnapshot snapshot)
    {
        try
        {
            if (e.Data.GetDataPresent(SiteInformationUrlDragFormat)
                && e.Data.GetData(SiteInformationUrlDragFormat) is string json
                && JsonSerializer.Deserialize<SiteInformationUrlDragSnapshot>(json) is { } parsed
                && parsed.SourceWindowId != Guid.Empty
                && parsed.SourceTabId != Guid.Empty
                && TryNormalizeDroppedUrl(parsed.Url ?? string.Empty, out var url))
            {
                parsed.Url = url;
                snapshot = parsed;
                return true;
            }
        }
        catch
        {
            // 別プロセスから届くドラッグデータは信頼せず、標準URL形式へフォールバックする。
        }

        snapshot = default!;
        return false;
    }

    private bool IsSameSiteInformationSource(
        SiteInformationUrlDragSnapshot snapshot,
        BrowserTab? targetTab)
    {
        return targetTab is not null
            && snapshot.SourceWindowId == _workspaceWindowId
            && snapshot.SourceTabId == targetTab.Id;
    }

    private async Task<bool> TryHandleSiteInformationUrlDropAsync(
        DragEventArgs e,
        bool dropOnTabList)
    {
        if (!TryGetSiteInformationUrlDragSnapshot(e, out var snapshot))
        {
            return false;
        }

        var targetTab = FindTabFromOriginalSource(e.OriginalSource);
        if (IsSameSiteInformationSource(snapshot, targetTab)
            || !dropOnTabList
            && targetTab is null
            && snapshot.SourceWindowId == _workspaceWindowId
            && GetActiveBrowserCommandTarget()?.Id == snapshot.SourceTabId)
        {
            SetStatus("元のタブへのURLドロップをキャンセルしました", autoClear: true);
            return false;
        }

        if (targetTab is { IsNativeContentTab: false })
        {
            SelectTab(targetTab);
            await NavigateTabToUrlAsync(targetTab, snapshot.Url!);
            SetStatus("ドロップ先のタブでURLを開きました", autoClear: true);
            return true;
        }

        var dropPoint = e.GetPosition(TabList);
        var insertionIndex = dropOnTabList || targetTab is not null
            ? GetInsertionIndexForDrop(dropPoint, e.OriginalSource)
            : (int?)null;
        var previousPositions = CaptureTabLayoutPositions();
        OpenTab(
            snapshot.Url!,
            activate: true,
            insertionIndex: insertionIndex,
            containerProfileId: GetActiveBrowserCommandTarget()?.ContainerProfileId);
        AnimateTabLayoutFrom(previousPositions);
        SetStatus("ドロップ先のWindowでURLを開きました", autoClear: true);
        return true;
    }

    private bool TryGetExternalTabDrop(DragEventArgs e, out ExternalTabDropTarget externalDrop)
    {
        if (TryGetFolderFileDropPaths(e.Data, out _))
        {
            externalDrop = default!;
            return false;
        }

        string? explicitUrl = null;
        foreach (var format in ExternalUrlDropFormats)
        {
            if (TryGetDropText(e, format, out var text)
                && TryExtractDroppedUrl(text, out var url))
            {
                explicitUrl = url;
                break;
            }
        }

        string? plainText = null;
        foreach (var format in ExternalTextDropFormats)
        {
            if (TryGetDropText(e, format, out var text))
            {
                plainText = text;
                break;
            }
        }

        var resolvedDrop = _tabDragDropService.ResolveExternalDrop(explicitUrl, plainText);
        if (resolvedDrop is null)
        {
            externalDrop = default!;
            return false;
        }

        externalDrop = resolvedDrop;
        return true;
    }

    private static bool TryGetDropText(DragEventArgs e, string format, out string text)
    {
        text = string.Empty;
        try
        {
            if (!e.Data.GetDataPresent(format))
            {
                return false;
            }

            var data = e.Data.GetData(format);
            switch (data)
            {
                case string value:
                    text = value;
                    return !string.IsNullOrWhiteSpace(text);
                case byte[] bytes:
                    text = DecodeDroppedUrlBytes(bytes, format);
                    return !string.IsNullOrWhiteSpace(text);
                case MemoryStream memoryStream:
                    text = DecodeDroppedUrlBytes(memoryStream.ToArray(), format);
                    return !string.IsNullOrWhiteSpace(text);
                case Stream stream:
                    text = ReadDroppedUrlStream(stream, format);
                    return !string.IsNullOrWhiteSpace(text);
                default:
                    return false;
            }
        }
        catch
        {
            text = string.Empty;
            return false;
        }
    }

    private static string ReadDroppedUrlStream(Stream stream, string format)
    {
        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

        using var memoryStream = new MemoryStream();
        stream.CopyTo(memoryStream);
        return DecodeDroppedUrlBytes(memoryStream.ToArray(), format);
    }

    private static string DecodeDroppedUrlBytes(byte[] bytes, string format)
    {
        var encoding = format.Equals("UniformResourceLocatorW", StringComparison.OrdinalIgnoreCase)
            ? System.Text.Encoding.Unicode
            : System.Text.Encoding.UTF8;
        return encoding.GetString(bytes).TrimEnd('\0');
    }

    private static bool TryExtractDroppedUrl(string text, out string url)
    {
        foreach (var line in text
            .Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = line.Trim().Trim('\0').Trim('"', '\'');
            if (TryNormalizeDroppedUrl(candidate, out url))
            {
                return true;
            }
        }

        url = string.Empty;
        return false;
    }

    private static bool TryNormalizeDroppedUrl(string value, out string url)
    {
        url = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var absoluteUri)
            && IsAllowedDroppedUrlScheme(absoluteUri.Scheme))
        {
            url = absoluteUri.ToString();
            return true;
        }

        if (LooksLikeDroppedHost(value))
        {
            url = "https://" + value;
            return true;
        }

        return false;
    }

    private static bool IsAllowedDroppedUrlScheme(string scheme)
    {
        return scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || scheme.Equals(Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase)
            || scheme.Equals("about", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeDroppedHost(string value)
    {
        return !value.Any(char.IsWhiteSpace)
            && (value.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("localhost:", StringComparison.OrdinalIgnoreCase)
                || value.Contains('.', StringComparison.Ordinal));
    }

    private static bool TryDeserializeTabTransferSnapshot(string json, out TabTransferSnapshot snapshot)
    {
        try
        {
            snapshot = JsonSerializer.Deserialize<TabTransferSnapshot>(json) ?? new TabTransferSnapshot();
            return !string.IsNullOrWhiteSpace(snapshot.Url);
        }
        catch
        {
            snapshot = default!;
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, int dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    private static bool IsLeftMouseButtonDown()
    {
        const int leftMouseButton = 0x01;
        return IsVirtualKeyDown(leftMouseButton);
    }

    private static bool IsRightMouseButtonDown()
    {
        const int rightMouseButton = 0x02;
        return IsVirtualKeyDown(rightMouseButton);
    }

    private static bool IsControlKeyDown()
    {
        const int controlKey = 0x11;
        return IsVirtualKeyDown(controlKey);
    }

    private static bool IsVirtualKeyDown(int virtualKey)
    {
        return (GetAsyncKeyState(virtualKey) & unchecked((short)0x8000)) != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;

        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;

        public int Top;

        public int Right;

        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelMouseHookData
    {
        public NativePoint Point;

        public int MouseData;

        public int Flags;

        public int Time;

        public IntPtr ExtraInfo;
    }

    private BrowserTab? FindTabFromOriginalSource(object originalSource)
    {
        return FindVisualParent<ListBoxItem>(originalSource as DependencyObject)?.DataContext is BrowserTab tab
            ? tab
            : null;
    }

    private bool IsPointOutsideWindow(Point point)
    {
        return point.X < 0
            || point.Y < 0
            || point.X > ActualWidth
            || point.Y > ActualHeight;
    }

    private bool IsCursorOverOtherVisibleTabBrowserWindow()
    {
        if (!TryGetCursorDevicePoint(out var devicePoint))
        {
            return false;
        }

        var sourcePoint = PointFromScreen(devicePoint);
        if (!IsPointOutsideWindow(sourcePoint))
        {
            return false;
        }

        foreach (var window in Application.Current.Windows.OfType<MainWindow>())
        {
            if (window == this
                || !window.IsVisible
                || window.WindowState == WindowState.Minimized
                || PresentationSource.FromVisual(window) is null)
            {
                continue;
            }

            var point = window.PointFromScreen(devicePoint);
            if (!window.IsPointOutsideWindow(point))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryGetCursorPointInTabList(ListBox tabList, out Point point)
    {
        if (!TryGetCursorDevicePoint(out var devicePoint)
            || PresentationSource.FromVisual(tabList) is null)
        {
            point = default;
            return false;
        }

        point = tabList.PointFromScreen(devicePoint);
        return true;
    }

    private bool TryGetCursorPointInAnyTabList(out ListBox tabList, out Point point)
    {
        foreach (var candidate in GetVisibleTabLists())
        {
            if (TryGetCursorPointInTabList(candidate, out point)
                && !_tabDragDropService.IsPointOutsideBounds(
                    point.X,
                    point.Y,
                    candidate.ActualWidth,
                    candidate.ActualHeight,
                    tolerance: 0))
            {
                tabList = candidate;
                return true;
            }
        }

        tabList = TabList;
        point = default;
        return false;
    }

    private bool IsPointOutsideTabReorderDropZone(ListBox tabList, Point point)
        => _tabDropController.IsPointOutsideTabReorderDropZone(tabList, point);

    private ListBox GetActiveTabDragList()
    {
        return _tabDragController?.ActiveList ?? (IsVerticalTabMode() ? VerticalTabList : TabList);
    }

    private ListBox GetVisibleTabListForTab(BrowserTab tab)
        => _tabStripController.GetVisibleTabListForTab(tab);

    private IEnumerable<ListBox> GetVisibleTabLists()
        => _tabStripController.GetVisibleTabLists();

    private ListBox ResolveTabList(object originalSource)
    {
        return FindVisualParent<ListBox>(originalSource as DependencyObject)
            ?? GetActiveTabDragList();
    }

    private bool CanDropTabOnList(BrowserTab tab, ListBox tabList)
    {
        if (ReferenceEquals(tabList, VerticalPinnedTabList))
        {
            return tab.IsPinned;
        }

        if (ReferenceEquals(tabList, VerticalTabList)
            && _settings.TabLayout.ShowPinnedTabsSection)
        {
            return !tab.IsPinned;
        }

        return true;
    }

    private static bool IsVerticalTabList(ListBox tabList)
        => TabStripController.IsVerticalTabList(tabList);

    private static T? FindVisualParent<T>(DependencyObject? source)
        where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T target)
            {
                return target;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private static T? FindNamedVisualParent<T>(DependencyObject? source, string name)
        where T : FrameworkElement
    {
        while (source is not null)
        {
            if (source is T target && target.Name == name)
            {
                return target;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private static string? GetJsonString(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static double? GetJsonDouble(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var value) && value.TryGetDouble(out var result)
            ? result
            : null;
    }

    private static bool GetJsonBool(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.True;
    }

    private static bool? GetJsonNullableBool(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private sealed class ExtensionToolbarItem : INotifyPropertyChanged
    {
        private bool _isExtensionEnabled;

        public ExtensionToolbarItem(
            CoreWebView2BrowserExtension extension,
            ExtensionManifestInfo? manifestInfo,
            ImageSource? icon)
        {
            Extension = extension;
            ManifestInfo = manifestInfo;
            Icon = icon;
            _isExtensionEnabled = extension.IsEnabled;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public CoreWebView2BrowserExtension Extension { get; }

        public ExtensionManifestInfo? ManifestInfo { get; }

        public ImageSource? Icon { get; }

        public bool HasIcon => Icon is not null;

        public string Id => Extension.Id;

        public string Name => Extension.Name;

        public string Initial
        {
            get
            {
                var trimmed = Name.Trim();
                return trimmed.Length == 0
                    ? "?"
                    : trimmed[0].ToString().ToUpper(CultureInfo.CurrentCulture);
            }
        }

        public bool IsExtensionEnabled
        {
            get => _isExtensionEnabled;
            set
            {
                if (_isExtensionEnabled == value)
                {
                    return;
                }

                _isExtensionEnabled = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExtensionEnabled)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EnabledText)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ToolTipText)));
            }
        }

        public string EnabledText => IsExtensionEnabled ? "ON" : "OFF";

        public string ToolTipText => $"{Name} ({EnabledText})";

        public string? GetSettingsUrl()
        {
            var page = ManifestInfo?.OptionsPage;
            if (string.IsNullOrWhiteSpace(page) && Name.Contains("uBlock", StringComparison.OrdinalIgnoreCase))
            {
                page = "dashboard.html";
            }

            if (string.IsNullOrWhiteSpace(page))
            {
                page = "options.html";
            }

            return ExtensionManifestInfo.BuildExtensionUrl(Id, page);
        }

        public string? GetPopupUrl()
        {
            return ExtensionManifestInfo.BuildExtensionUrl(Id, ManifestInfo?.DefaultPopupPage);
        }
    }

    private sealed class WebViewControlEventSubscription(
        MainWindow owner,
        BrowserTab tab,
        WebViewEventSubscription subscription) : IDisposable
    {
        private bool _isDisposed;

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            owner.DetachWebViewEventHandlers(tab, subscription);
        }
    }

    private sealed class CoreWebViewRuntimeEventSubscription(
        MainWindow owner,
        BrowserTab tab,
        CoreWebViewEventSubscription subscription) : IDisposable
    {
        private bool _isDisposed;

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            owner.DetachCoreWebViewEventHandlers(tab, subscription);
        }
    }

    private sealed class WebViewRuntimeEventSubscription(
        IDisposable webViewSubscription,
        IDisposable? coreSubscription) : IDisposable
    {
        private bool _isDisposed;

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            List<Exception>? exceptions = null;
            if (coreSubscription is not null)
            {
                try
                {
                    coreSubscription.Dispose();
                }
                catch (Exception ex)
                {
                    (exceptions ??= []).Add(ex);
                }
            }

            try
            {
                webViewSubscription.Dispose();
            }
            catch (Exception ex)
            {
                (exceptions ??= []).Add(ex);
            }

            if (exceptions is { Count: 1 })
            {
                throw exceptions[0];
            }

            if (exceptions is { Count: > 1 })
            {
                throw new AggregateException("WebView2イベント購読をすべて解除できませんでした。", exceptions);
            }
        }
    }

    private sealed class WebViewEventSubscription : IDisposable
    {
        private bool _isAttached;

        public WebViewEventSubscription(
            WebView2 webView,
            RoutedEventHandler gotFocus,
            KeyboardFocusChangedEventHandler gotKeyboardFocus,
            EventHandler<EventArgs> zoomFactorChanged,
            EventHandler<CoreWebView2NavigationStartingEventArgs> navigationStarting,
            EventHandler<CoreWebView2NavigationCompletedEventArgs> navigationCompleted)
        {
            WebView = webView;
            GotFocus = gotFocus;
            GotKeyboardFocus = gotKeyboardFocus;
            ZoomFactorChanged = zoomFactorChanged;
            NavigationStarting = navigationStarting;
            NavigationCompleted = navigationCompleted;
        }

        public WebView2 WebView { get; }

        public RoutedEventHandler GotFocus { get; }

        public KeyboardFocusChangedEventHandler GotKeyboardFocus { get; }

        public EventHandler<EventArgs> ZoomFactorChanged { get; }

        public EventHandler<CoreWebView2NavigationStartingEventArgs> NavigationStarting { get; }

        public EventHandler<CoreWebView2NavigationCompletedEventArgs> NavigationCompleted { get; }

        public void Attach()
        {
            if (_isAttached)
            {
                return;
            }

            _isAttached = true;
            try
            {
                WebView.GotFocus += GotFocus;
                WebView.GotKeyboardFocus += GotKeyboardFocus;
                WebView.ZoomFactorChanged += ZoomFactorChanged;
                WebView.NavigationStarting += NavigationStarting;
                WebView.NavigationCompleted += NavigationCompleted;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (!_isAttached)
            {
                return;
            }

            _isAttached = false;
            TryDetach(() => WebView.GotFocus -= GotFocus);
            TryDetach(() => WebView.GotKeyboardFocus -= GotKeyboardFocus);
            TryDetach(() => WebView.ZoomFactorChanged -= ZoomFactorChanged);
            TryDetach(() => WebView.NavigationStarting -= NavigationStarting);
            TryDetach(() => WebView.NavigationCompleted -= NavigationCompleted);
        }
    }

    private sealed class CoreWebViewEventSubscription : IDisposable
    {
        private bool _isAttached;

        public CoreWebViewEventSubscription(
            CoreWebView2 core,
            EventHandler<CoreWebView2FrameCreatedEventArgs> frameCreated,
            EventHandler<object> documentTitleChanged,
            EventHandler<CoreWebView2SourceChangedEventArgs> sourceChanged,
            EventHandler<object> historyChanged,
            EventHandler<object> faviconChanged,
            EventHandler<object> isDocumentPlayingAudioChanged,
            EventHandler<object> isMutedChanged,
            EventHandler<object> containsFullScreenElementChanged,
            EventHandler<CoreWebView2ContextMenuRequestedEventArgs> contextMenuRequested,
            EventHandler<object> windowCloseRequested,
            EventHandler<CoreWebView2NewWindowRequestedEventArgs> newWindowRequested,
            EventHandler<CoreWebView2PermissionRequestedEventArgs> permissionRequested,
            EventHandler<CoreWebView2ScreenCaptureStartingEventArgs> screenCaptureStarting,
            EventHandler<CoreWebView2DownloadStartingEventArgs> downloadStarting,
            EventHandler<CoreWebView2WebMessageReceivedEventArgs> webMessageReceived,
            EventHandler<CoreWebView2ProcessFailedEventArgs> processFailed)
        {
            Core = core;
            FrameCreated = frameCreated;
            DocumentTitleChanged = documentTitleChanged;
            SourceChanged = sourceChanged;
            HistoryChanged = historyChanged;
            FaviconChanged = faviconChanged;
            IsDocumentPlayingAudioChanged = isDocumentPlayingAudioChanged;
            IsMutedChanged = isMutedChanged;
            ContainsFullScreenElementChanged = containsFullScreenElementChanged;
            ContextMenuRequested = contextMenuRequested;
            WindowCloseRequested = windowCloseRequested;
            NewWindowRequested = newWindowRequested;
            PermissionRequested = permissionRequested;
            ScreenCaptureStarting = screenCaptureStarting;
            DownloadStarting = downloadStarting;
            WebMessageReceived = webMessageReceived;
            ProcessFailed = processFailed;
        }

        public CoreWebView2 Core { get; }

        public EventHandler<CoreWebView2FrameCreatedEventArgs> FrameCreated { get; }

        public EventHandler<object> DocumentTitleChanged { get; }

        public EventHandler<CoreWebView2SourceChangedEventArgs> SourceChanged { get; }

        public EventHandler<object> HistoryChanged { get; }

        public EventHandler<object> FaviconChanged { get; }

        public EventHandler<object> IsDocumentPlayingAudioChanged { get; }

        public EventHandler<object> IsMutedChanged { get; }

        public EventHandler<object> ContainsFullScreenElementChanged { get; }

        public EventHandler<CoreWebView2ContextMenuRequestedEventArgs> ContextMenuRequested { get; }

        public EventHandler<object> WindowCloseRequested { get; }

        public EventHandler<CoreWebView2NewWindowRequestedEventArgs> NewWindowRequested { get; }

        public EventHandler<CoreWebView2PermissionRequestedEventArgs> PermissionRequested { get; }

        public EventHandler<CoreWebView2ScreenCaptureStartingEventArgs> ScreenCaptureStarting { get; }

        public EventHandler<CoreWebView2DownloadStartingEventArgs> DownloadStarting { get; }

        public EventHandler<CoreWebView2WebMessageReceivedEventArgs> WebMessageReceived { get; }

        public EventHandler<CoreWebView2ProcessFailedEventArgs> ProcessFailed { get; }

        public void Attach()
        {
            if (_isAttached)
            {
                return;
            }

            _isAttached = true;
            try
            {
                Core.FrameCreated += FrameCreated;
                Core.DocumentTitleChanged += DocumentTitleChanged;
                Core.SourceChanged += SourceChanged;
                Core.HistoryChanged += HistoryChanged;
                Core.FaviconChanged += FaviconChanged;
                Core.IsDocumentPlayingAudioChanged += IsDocumentPlayingAudioChanged;
                Core.IsMutedChanged += IsMutedChanged;
                Core.ContainsFullScreenElementChanged += ContainsFullScreenElementChanged;
                Core.ContextMenuRequested += ContextMenuRequested;
                Core.WindowCloseRequested += WindowCloseRequested;
                Core.NewWindowRequested += NewWindowRequested;
                Core.PermissionRequested += PermissionRequested;
                Core.ScreenCaptureStarting += ScreenCaptureStarting;
                Core.DownloadStarting += DownloadStarting;
                Core.WebMessageReceived += WebMessageReceived;
                Core.ProcessFailed += ProcessFailed;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (!_isAttached)
            {
                return;
            }

            _isAttached = false;
            TryDetach(() => Core.FrameCreated -= FrameCreated);
            TryDetach(() => Core.DocumentTitleChanged -= DocumentTitleChanged);
            TryDetach(() => Core.SourceChanged -= SourceChanged);
            TryDetach(() => Core.HistoryChanged -= HistoryChanged);
            TryDetach(() => Core.FaviconChanged -= FaviconChanged);
            TryDetach(() => Core.IsDocumentPlayingAudioChanged -= IsDocumentPlayingAudioChanged);
            TryDetach(() => Core.IsMutedChanged -= IsMutedChanged);
            TryDetach(() => Core.ContainsFullScreenElementChanged -= ContainsFullScreenElementChanged);
            TryDetach(() => Core.ContextMenuRequested -= ContextMenuRequested);
            TryDetach(() => Core.WindowCloseRequested -= WindowCloseRequested);
            TryDetach(() => Core.NewWindowRequested -= NewWindowRequested);
            TryDetach(() => Core.PermissionRequested -= PermissionRequested);
            TryDetach(() => Core.ScreenCaptureStarting -= ScreenCaptureStarting);
            TryDetach(() => Core.DownloadStarting -= DownloadStarting);
            TryDetach(() => Core.WebMessageReceived -= WebMessageReceived);
            TryDetach(() => Core.ProcessFailed -= ProcessFailed);
        }
    }

    private static void TryDetach(Action detach)
    {
        try
        {
            detach();
        }
        catch
        {
            // WebView2終了と競合したイベントはRuntime破棄に任せる。
        }
    }

    private sealed class TabDragPayload(
        MainWindow sourceWindow,
        BrowserTab tab,
        bool isInteractiveDrag = false) : ITabDragOperation, ITabDropPayload, ITabTransferPayload
    {
        TabTransferController ITabTransferPayload.Source => SourceWindow._tabTransferController;

        void ITabTransferPayload.UpdateSource(ITabTransferHost target, BrowserTab tab)
            => UpdateSource((MainWindow)target, tab);

        bool ITabDropPayload.IsFrom(ITabDropHost host) => ReferenceEquals(SourceWindow, host);

        int ITabDropPayload.GroupCount => SourceWindow.GetTabDragGroup(Tab).Count;

        private DataObject? _interactiveDragData;

        public bool IsSourceOriginal => SourceWindow == OriginalSourceWindow;

        public void PrepareInteractiveDragData()
        {
            var data = new DataObject();
            data.SetData(typeof(TabDragPayload), this);
            data.SetData(typeof(BrowserTab), Tab);
            OriginalSourceWindow.SetTabTransferData(data, Tab);
            _interactiveDragData = data;
        }

        public DragDropEffects Run(ListBox dragList)
            => DragDrop.DoDragDrop(dragList, _interactiveDragData!, DragDropEffects.Move | DragDropEffects.Copy);

        public bool TryTransferOriginalSourceAtCursor()
            => OriginalSourceWindow.TryTransferTabToOtherWindowAtCursor(this);

        public void TryTransferCurrentSourceAtCursor()
            => SourceWindow.TryTransferTabToOtherWindowAtCursor(this);

        public void DetachOriginalTab(BrowserTab draggedTab)
            => OriginalSourceWindow.DetachTabToNewWindow(draggedTab, this);

        public MainWindow OriginalSourceWindow { get; } = sourceWindow;

        public int OriginalInsertionIndex { get; } = Math.Max(0, sourceWindow._tabs.IndexOf(tab));

        public bool IsInteractiveDrag { get; } = isInteractiveDrag;

        public MainWindow SourceWindow { get; private set; } = sourceWindow;

        public BrowserTab Tab { get; private set; } = tab;

        public void UpdateSource(MainWindow sourceWindow, BrowserTab tab)
        {
            SourceWindow = sourceWindow;
            Tab = tab;
        }
    }

    private sealed class TabTransferSnapshot : ITabDropSnapshot
    {
        bool ITabDropSnapshot.IsWorkspaceTab => IsWorkspaceTabUrl(Url);

        public string? Url { get; set; }

        public string? Title { get; set; }

        public string? ContainerProfileId { get; set; }

        public bool IsPrivateWindow { get; set; }
    }

    private sealed class SiteInformationUrlDragSnapshot : ITabDropSiteInformation
    {
        public Guid SourceWindowId { get; set; }

        public Guid SourceTabId { get; set; }

        public string? Url { get; set; }
    }
}
