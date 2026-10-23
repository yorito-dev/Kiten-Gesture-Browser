namespace TabBrowser.Services;

internal enum WindowTaskShutdownDisposition
{
    DrainBeforeWindowDispose,
    DiscardAtWindowShutdown,
    ContinueWithIndependentOwner
}

internal sealed record WindowTaskOwnershipEntry(
    string LaunchKey,
    int ExpectedLaunchCount,
    WindowTaskShutdownDisposition Disposition,
    string Owner,
    string Rationale);

internal static class WindowTaskOwnershipPolicy
{
    internal static IReadOnlyList<WindowTaskOwnershipEntry> Entries { get; } =
    [
        Drain("amazon-prime-video-readiness"),
        Drain("address-suggestion-open"),
        Drain("background-tab-load"),
        Drain("background-thumbnail-warmup"),
        Drain("bookmark-delete"),
        Drain("bookmark-open"),
        Drain("bookmark-preview-show"),
        Drain("browser-shortcut-command"),

        Drain("closed-tab-runtime-suspend"),
        Drain("closed-tab-thumbnail-capture"),
        Drain("command-palette-tab-thumbnail"),
        Drain("command-palette-url-thumbnail"),
        Drain("command-palette-execute"),
        Drain("extension-toolbar-refresh"),
        Drain("folder-tab-navigate-back"),
        Drain("folder-tab-navigate-forward"),
        Drain("folder-tab-reload"),
        Drain("folder-tab-split-open"),
        Drain("focused-viewing-cpu-saver"),
        Drain("gesture-command-execute"),
        Drain("inactive-youtube-tab-check"),
        Drain("link-preview-show"),
        Drain("new-tab-search-navigation"),
        Drain("right-wheel-tab-activation-settle"),
        Drain("search-active-page-selection"),
        Drain("search-selection-command"),
        Drain("selected-tab-activation"),
        Drain("split-view-display-refresh-delay"),
        Drain("split-youtube-control-pane-update"),
        Drain("split-youtube-control-panel-refresh-delays"),
        Drain("startup-selected-tab-activation"),

        Drain("tab-audio-pan-apply"),
        Drain("tab-audio-pan-close-reset"),
        Drain("tab-audio-pan-frame-apply"),
        Drain("tab-audio-pan-frame-initialize"),
        Drain("tab-audio-pan-retries"),
        Drain("tab-drag-preview-thumbnail-capture"),
        Drain("tab-favicon-refresh"),
        Drain("tab-memory-restore-after-navigation"),
        Drain("web-context-link-bottom-split-open"),
        Drain("web-context-link-right-split-open"),
        Drain("web-context-page-paste"),
        Drain("web-context-split-pane-replace"),
        Drain("webview-gpu-media-recovery"),
        Drain("webview-process-auto-recovery"),
        Drain("webview-process-group-recovery"),
        Drain("webview-render-process-reload"),
        Drain("youtube-application-fullscreen-exit"),
        Drain("youtube-control-popup-placement"),
        Drain("youtube-mini-player-layout-verification"),
        Drain("youtube-shorts-history-replace-navigation"),

        Continue("MainWindow.ClipboardHistory.cs", "OpenSettingsDialogAsync", 1,
            "ModelessWindowService", "表示Taskはserviceが追跡するSettings WindowのClosedまで待つだけで、補助Windowの寿命はserviceが所有する。"),

        AuditedDiscard(
            "MainWindow.CommandPalette.cs",
            "LoadCommandPaletteSelectionContextAsync",
            1,
            "CommandPaletteContextCoordinator",
            "Coordinator generation、palette表示、tab所属、context tab同一性、Window終了状態をawait後に照合し、古いselection結果はCoordinatorが拒否する。"),
        Continue(
            "CommandPaletteViewController.cs",
            "ShowCommandPaletteQuickLookAsync",
            1,
            "CommandPaletteQuickLookHost / QuickLookService",
            "QuickLookHost.ShowAsyncが_loadVersion、_isDisposed、IsOpenとservice cancellationで古い読込結果を破棄し、専用hostが表示Taskの寿命を所有する。"),

        Continue("MainWindow.FolderTabs.cs", "Dispatcher.InvokeAsync", 3,
            "WPF Dispatcher", "別threadからのUI marshalであり、Dispatcherがoperationの実行可否と寿命を所有する。"),
        AuditedDiscard(
            "MainWindow.GestureOverlay.cs",
            "CancelActiveGestureOverlayAsync",
            2,
            "対象WebView Core",
            "gesture overlay解除の一回限りのCore scriptでawait後のMainWindow変更はない。native確定／右wheel取消は同じhost adapterへ接続し、遷移・Core破棄失敗を内部で許容する。"),
        AuditedDiscard(
            "MainWindow.GestureOverlay.cs",
            "CancelPageGestureSourceAfterNativeReleaseAsync",
            1,
            "InputInteractionHub interaction ID",
            "遅延後にinteraction IDの現行性を確認し、Window終了時のCancelForWindowで無効化されるため古い要求はpage/Core解除へ進まない。"),
        AuditedDiscard(
            "MainWindow.GestureOverlay.cs",
            "CancelWorkspaceGestureAfterNativeReleaseAsync",
            1,
            "InputInteractionHub interaction ID",
            "遅延後にinteraction IDの現行性を確認し、Window終了時のCancelForWindowで無効化されるため古い要求はWorkspace gesture状態を変更しない。"),
        AuditedDiscard(
            "MainWindow.GestureOverlay.cs",
            "UseNativePageGestureVisualAsync",
            2,
            "対象WebView Core / native gesture interaction",
            "native gestureの現行状態から開始する一回限りのCore表示scriptでawait後のMainWindow変更はなく、遷移・Core破棄失敗を内部で許容する。"),

        AuditedDiscard(
            "PageToolsController.cs",
            "RestartPageFindAsync",
            2,
            "PageToolsWorkCoordinator / CoreWebView2Find session",
            "Coordinator世代、find session同一性、Popup表示をawait後に照合し、StopPageFindSessionとDisposePageToolsが世代を無効化して古い完了・失敗のUI反映を拒否する。"),
        AuditedDiscard(
            "RecentItemThumbnailController.cs",
            "RemoveRecentItemThumbnailFetchAsync",
            1,
            "RecentItemThumbnailController / Task identity",
            "取得本体はWindow lifetime tokenを使用し、生継続はfinallyで辞書内の同一Taskだけを条件付き削除してPopup、WebView、UI、資源を変更しない。"),

        AuditedDiscard(
            "MainWindow.WebContextMenu.cs",
            "FinishPageContextMenuGestureAsync",
            1,
            "InputInteractionHub interaction ID",
            "一回限りのCore完了scriptで失敗を許容し、finallyでinteraction所有権を必ず完了するため終了後にuser commandを実行しない。"),
        AuditedDiscard(
            "MainWindow.WebContextMenu.cs",
            "StartPageContextMenuGestureAsync",
            1,
            "InputInteractionHub interaction ID",
            "Core script結果とinteraction ID現行性を照合し、Window終了時のCancelForWindow後は古い結果からgesture commandへ進まずinteractionを完了する。"),

        AuditedDiscard(
            "MainWindow.xaml.cs",
            "_youTubeControlService.SetPlaybackStateBridgeActiveAsync",
            2,
            "YouTubeControlService / 対象WebView Core",
            "active tab切替時の一回限りのbridge scriptでawait後のMainWindow変更はなく、遷移・Core破棄失敗をserviceが許容し次のactivationで状態を再設定する。"),

        AuditedDiscard(
            "MainWindow.xaml.cs",
            "CancelActiveGestureOverlayAsync",
            3,
            "対象WebView Core",
            "gesture overlay解除の一回限りのCore scriptでawait後のMainWindow変更はなく、ページ遷移・Core破棄失敗を内部で許容する。"),
        Continue("MainWindow.xaml.cs", "Dispatcher.InvokeAsync", 1,
            "WPF Dispatcher", "別threadからのUI marshalであり、Dispatcherがoperationの実行可否と寿命を所有する。"),
        Continue("MainWindow.xaml.cs", "existingWindow.ShowItemAsync", 1,
            "WorkspaceFolderWindow", "既存の別Windowへ委譲した表示要求であり、対象Windowが要求の寿命を所有する。"),
        Continue("MainWindow.xaml.cs", "HandleScreenCaptureStartingAsync", 1,
            "CoreWebView2 deferral", "WebView2から取得したdeferralを必ず完了する処理であり、callback契約が完了寿命を所有する。"),
        Continue("MainWindow.xaml.cs", "HandleSitePermissionRequestedAsync", 1,
            "CoreWebView2 deferral", "WebView2から取得したdeferralを必ず完了する処理であり、callback契約が完了寿命を所有する。"),
        Continue("MainWindow.xaml.cs", "OpenNewWindowRequestInForegroundTabWithDeferralAsync", 1,
            "CoreWebView2 deferral", "NewWindowRequestedのdeferral完了までをWebView2 callback契約が所有する。"),

        AuditedDiscard(
            "MainWindow.xaml.cs",
            "TryExitDocumentFullscreenAsync",
            1,
            "対象WebView Core",
            "tab選択変更時だけ生起動する一回限りのdocument fullscreen解除scriptで全失敗を内部処理し、await後のMainWindow変更はなくapplication fullscreen復元は呼出側が同期実行する。"),

        ContinueMiniPlayer("_youTubeMiniPlayerController.OpenFromLinkAsync", 1, "YouTubeMiniPlayerController"),
        Continue("MainWindow.WebContextMenu.cs", "OpenTabInYouTubeMiniPlayerAsync", 1,
            "YouTubeMiniPlayerController", "ページメニューからタブメニューと同じ開始処理へ委譲し、既存のmini-player Runtimeに寿命管理を任せる。"),
        Continue("YouTubeMiniPlayerActivityController.cs", "ApplyYouTubeMiniPlayerSizeBasedQualityAsync", 2,
            "mini-player Runtime", "品質ControllerがRuntimeの既存Timerと適用signatureを使い、RuntimeDisposeでTimerを停止する。"),
        ContinueMiniPlayer("CancelYouTubeMiniPlayerRightInputAsync", 1, "mini-player input generation"),
        Continue("YouTubeMiniPlayerListViewController.cs", "EnsureYouTubeMiniPlayerListThumbnailAsync", 2,
            "mini-player thumbnail request CTS", "一覧Controllerの既存batch CTSと項目のsource key・世代が取得と結果反映を所有する。"),
        ContinueMiniPlayer("ExecuteYouTubeMiniPlayerGestureAsync", 1, "mini-player Runtime gesture"),
        ContinueMiniPlayer("ExecuteYouTubeMiniPlayerGestureCommandAsync", 1, "mini-player Runtime gesture"),
        ContinueMiniPlayer("overlayPlacementCallbacks.UpdateToolbarPlacementAsync", 1, "mini-player overlay callback"),
        ContinueMiniPlayer("RefreshYouTubeLiveChatAvailabilityAsync", 1, "mini-player live-chat Runtime"),
        Continue("YouTubeMiniPlayerActivityController.cs", "_host.RefreshLiveChatAsync", 1,
            "mini-player live-chat Runtime", "再生状態通知から既存chat ControllerのRuntime／動画世代確認へ接続する。"),
        Continue("YouTubeMiniPlayerCommandController.cs", "_host.RefreshListAsync", 2,
            "mini-player list request generation", "全Window一括最小化／終了後の非待機一覧更新は、既存一覧Controllerの世代と取得排他へ接続する。"),
        Continue("YouTubeMiniPlayerArrangementController.cs", "host.RefreshListAsync", 1,
            "mini-player list request generation", "配置復元後は既存一覧Controllerの世代と取得排他へ接続する。"),
        Continue("LocalVideoMiniPlayerController.cs", "host.RefreshListAsync", 2,
            "mini-player list request generation", "ローカル動画の開始／差し替え後は既存一覧Controllerの世代と取得排他へ接続する。"),
        Continue("YouTubeMiniPlayerPersistenceController.cs", "_host.RefreshListAsync", 2,
            "mini-player list request generation", "状態復元ControllerのWindow記憶／Workspace復元後に、既存一覧Controllerの世代と取得排他へ接続する。"),
        Continue("YouTubeMiniPlayerLifetimeController.cs", "host.RestoreAudioFocusAsync", 1,
            "mini-player audio-focus owner", "終了Controllerから既存音声Controllerの共有gateと復元手順へ接続する。"),
        Continue("YouTubeMiniPlayerRecoveryController.cs", "RunYouTubeMiniPlayerAutoRecoveryAsync", 1,
            "YouTubeMiniPlayerRecoveryService", "復旧Controllerが既存serviceの要求取消・段階遷移とWindow lifetime tokenを共有し、RuntimeとWebViewの現行性を確認する。"),
        Continue("YouTubeMiniPlayerActivityController.cs", "RunYouTubeMiniPlayerInactiveSuspensionAsync", 1,
            "mini-player Runtime generation", "非アクティブControllerが既存Window lifetime tokenとRuntime世代を確認し、待機後の現行性と表示条件を守る。"),
        Continue("YouTubeMiniPlayerLifetimeController.cs", "host.StopAudioPreviewAsync", 1,
            "mini-player audio-preview owner", "終了Controllerから既存音声Controllerの共有gateとpreview停止手順へ接続する。"),
        Continue("YouTubeMiniPlayerListViewController.cs", "_host.StopHoverPreviewAsync", 1,
            "mini-player audio-preview owner", "hover離脱から既存の共有音声preview終了手順へ委譲し、二重のTask監督を追加しない。"),
        Continue("YouTubeMiniPlayerListPopupController.cs", "_host.StopHoverPreviewAsync", 1,
            "mini-player audio-preview owner", "Popup内クリックから既存の共有音声preview終了手順へ委譲し、二重のTask監督を追加しない。"),
        ContinueMiniPlayer("window.RefreshYouTubeMiniPlayerListAsync", 1, "target mini-player host Window"),
        ContinueMiniPlayer("YouTubeMiniPlayerScriptExecutionService.ExecuteAsync", 3, "mini-player script execution service")
    ];

    private static WindowTaskOwnershipEntry Drain(string operation)
    {
        return new WindowTaskOwnershipEntry(
            $"supervisor:{operation}",
            1,
            WindowTaskShutdownDisposition.DrainBeforeWindowDispose,
            "WindowTaskSupervisor",
            "Window寿命のTaskとして登録され、shutdown cancel後に資源破棄より先に期限付きdrainされる。");
    }

    private static WindowTaskOwnershipEntry AuditedDiscard(
        string sourceFile,
        string callee,
        int expectedLaunchCount,
        string owner,
        string rationale)
    {
        return new WindowTaskOwnershipEntry(
            RawKey(sourceFile, callee),
            expectedLaunchCount,
            WindowTaskShutdownDisposition.DiscardAtWindowShutdown,
            owner,
            rationale);
    }

    private static WindowTaskOwnershipEntry Continue(
        string sourceFile,
        string callee,
        int expectedLaunchCount,
        string owner,
        string rationale)
    {
        return new WindowTaskOwnershipEntry(
            RawKey(sourceFile, callee),
            expectedLaunchCount,
            WindowTaskShutdownDisposition.ContinueWithIndependentOwner,
            owner,
            rationale);
    }

    private static WindowTaskOwnershipEntry ContinueMiniPlayer(
        string callee,
        int expectedLaunchCount,
        string owner)
    {
        return Continue(
            "MainWindow.YouTubeMiniPlayer.cs",
            callee,
            expectedLaunchCount,
            owner,
            "専用mini-player Runtime、要求世代、CTSまたはserviceが現行性と破棄を所有し、MainWindow共通Supervisorへ二重登録しない。");
    }

    private static string RawKey(string sourceFile, string callee)
    {
        return $"raw:{sourceFile}:{callee}";
    }
}
