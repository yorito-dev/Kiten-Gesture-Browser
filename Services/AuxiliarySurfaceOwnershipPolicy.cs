namespace TabBrowser.Services;

internal enum AuxiliarySurfaceKind
{
    Window,
    Popup
}

internal enum AuxiliarySurfaceOwner
{
    ModelessWindowService,
    MainWindow,
    MainWindowCollection,
    InputInteraction,
    FloatingMediaHost,
    MiniPlayerRuntime,
    XamlMainWindow,
    FeatureController
}

internal enum AuxiliarySurfaceTermination
{
    ClosedUnregister,
    BeginWindowShutdown,
    DisposeWindowResources,
    TabOrRequestCompletion,
    InputInteractionEnd,
    StopFloatingMedia,
    RuntimeDispose,
    ExplicitPopupClose
}

internal sealed record AuxiliarySurfaceOwnershipEntry(
    string SurfaceKey,
    AuxiliarySurfaceKind Kind,
    AuxiliarySurfaceOwner Owner,
    AuxiliarySurfaceTermination Termination,
    string Rationale);

internal static class AuxiliarySurfaceOwnershipPolicy
{
    internal static IReadOnlyList<AuxiliarySurfaceOwnershipEntry> Entries { get; } =
    [
        Modeless<SettingsWindow>(),
        Modeless<MouseGestureSettingsWindow>(),
        Modeless<BrowsingHistoryWindow>(),
        Modeless<ClipboardHistoryWindow>(),
        Modeless<SitePrivacyWindow>(),
        Modeless<RuntimeDiagnosticsWindow>(),

        WindowField(
            "MainWindow.xaml.cs:_windowCloseConfirmationWindow",
            AuxiliarySurfaceOwner.MainWindow,
            AuxiliarySurfaceTermination.BeginWindowShutdown,
            "Window終了確認の結果またはWindow終了開始時にMainWindowが閉じて参照を解除する。"),
        WindowField(
            "MainWindow.xaml.cs:_workspaceOSWindow",
            AuxiliarySurfaceOwner.MainWindow,
            AuxiliarySurfaceTermination.DisposeWindowResources,
            "MainWindowが単一のWorkspace Windowを保持し、Window資源破棄時に既存の終了経路で閉じる。"),
        WindowField(
            "MainWindow.xaml.cs:_workspaceFolderWindows",
            AuxiliarySurfaceOwner.MainWindowCollection,
            AuxiliarySurfaceTermination.DisposeWindowResources,
            "MainWindowがWorkspace ID別の辞書を所有し、Closed解除とWindow資源破棄で回収する。"),
        WindowField(
            "MainWindow.SitePrivacy.cs:_sitePermissionPrompts",
            AuxiliarySurfaceOwner.MainWindowCollection,
            AuxiliarySurfaceTermination.TabOrRequestCompletion,
            "MainWindowがtab別の要求Window群を保持し、要求完了、tab終了または全要求cancelで回収する。"),
        WindowField(
            "Views/WorkspaceNoteEditController.cs:_windows",
            AuxiliarySurfaceOwner.FeatureController,
            AuxiliarySurfaceTermination.ClosedUnregister,
            "WorkspaceNoteEditControllerがnote ID別の編集Windowを保持し、表示完了時に同一参照だけを辞書から除く。"),
        WindowField(
            "Views/PageGestureVisualController.cs:_pageGestureOverlayWindow",
            AuxiliarySurfaceOwner.FeatureController,
            AuxiliarySurfaceTermination.InputInteractionEnd,
            "表示ControllerがOverlayを保持し、interaction終了時は表示を解除して再利用し、Window終了時に実Windowを閉じる。"),
        WindowField(
            "MainWindow.YouTubeMiniPlayer.cs:_youTubeMiniPlayerArrangementPreviewWindow",
            AuxiliarySurfaceOwner.FloatingMediaHost,
            AuxiliarySurfaceTermination.StopFloatingMedia,
            "配置予告中だけFloatingMedia hostが保持し、配置確定、cancelまたはfloating-media終了経路で閉じる。"),
        WindowField(
            "Views/YouTubeLiveChatWindowController.cs:_youTubeLiveChatWindows",
            AuxiliarySurfaceOwner.FeatureController,
            AuxiliarySurfaceTermination.StopFloatingMedia,
            "ライブチャットControllerがmini-player ID別に保持し、対応Runtime終了またはfloating-media全体の終了時に閉じる。"),

        RuntimeWindow(
            "YouTubeMiniPlayerRuntime:TaskbarWindow",
            "mini-player RuntimeがTaskbar用Windowを一つ保持し、Runtime dispose経路で閉じる。"),
        RuntimePopup(
            "YouTubeMiniPlayerRuntime:Popup",
            "mini-player Runtimeが映像Popupを保持し、Runtime dispose経路で閉じる。"),
        RuntimePopup(
            "YouTubeMiniPlayerRuntime:TitleBandPopup",
            "mini-player Runtimeがタイトル帯Popupを保持し、Runtime dispose経路で閉じる。"),
        RuntimePopup(
            "YouTubeMiniPlayerRuntime:ToolbarPopup",
            "mini-player Runtimeが操作Toolbar Popupを保持し、Runtime dispose経路で閉じる。"),

        PopupField(
            "MainWindow.xaml.cs:_extensionPopup",
            AuxiliarySurfaceOwner.MainWindow,
            AuxiliarySurfaceTermination.DisposeWindowResources,
            "MainWindowが遅延生成した拡張PopupとWebViewを保持し、既存のcloseとWindow資源破棄で回収する。"),
        PopupField(
            "Views/BookmarkPreviewController.cs:_bookmarkPreviewPopup",
            AuxiliarySurfaceOwner.FeatureController,
            AuxiliarySurfaceTermination.DisposeWindowResources,
            "BookmarkPreviewControllerがPopupとWebViewを所有し、要求のcloseとWindow資源破棄の既存入口から回収する。"),
        PopupField(
            "Views/WorkspaceGestureController.cs:_popup",
            AuxiliarySurfaceOwner.InputInteraction,
            AuxiliarySurfaceTermination.InputInteractionEnd,
            "Workspace gesture controllerが軌跡Popupを保持し、interaction終了またはcontroller破棄時に閉じる。"),
        PopupField(
            "Views/WorkspaceGestureController.cs:_statusPopup",
            AuxiliarySurfaceOwner.InputInteraction,
            AuxiliarySurfaceTermination.InputInteractionEnd,
            "Workspace gesture controllerがstatus Popupを保持し、interaction終了またはcontroller破棄時に閉じる。"),

        XamlPopup("ClosedTabHistoryPopup"),
        XamlPopup("SiteInformationPopup"),
        XamlPopup("PageZoomPopup"),
        XamlPopup("YouTubeMiniPlayerListPopup"),
        XamlPopup("YouTubeMiniPlayerArrangePopup"),
        XamlPopup("YouTubeMiniPlayerGlobalActionsPopup"),
        XamlPopup("YouTubeMiniPlayerLinearPlacementPopup"),
        XamlPopup("YouTubeMiniPlayerItemMorePopup"),
        XamlPopup("YouTubeMiniPlayerSpeedPopup"),
        XamlPopup("YouTubeMiniPlayerAbRepeatPopup"),
        XamlPopup("TabAudioBoostPopup"),
        XamlPopup("YouTubeMiniPlayerVolumePopup"),
        XamlPopup("AmazonPrimeVideoNotice"),
        XamlPopup("PageFindPopup"),
        XamlPopup("StatusPopup"),
        XamlPopup("CommandPaletteOverlay"),
        XamlPopup("LinkPreviewPopup"),
        XamlPopup("YouTubeControlPopup"),
        XamlPopup("SplitLeftYouTubeControlPopup"),
        XamlPopup("SplitRightYouTubeControlPopup"),
        XamlPopup("ChatGptReadabilityPopup"),
        XamlPopup("DownloadShelfPopup"),
        XamlPopup("AddressSuggestionPopup"),

        XamlPopup("TabDragPopup")
    ];

    private static AuxiliarySurfaceOwnershipEntry Modeless<TWindow>()
    {
        return new AuxiliarySurfaceOwnershipEntry(
            $"modeless:{typeof(TWindow).Name}",
            AuxiliarySurfaceKind.Window,
            AuxiliarySurfaceOwner.ModelessWindowService,
            AuxiliarySurfaceTermination.ClosedUnregister,
            "MainWindowごとのsingletonをModelessWindowServiceが追跡し、Closedで登録を解除する。");
    }

    private static AuxiliarySurfaceOwnershipEntry WindowField(
        string key,
        AuxiliarySurfaceOwner owner,
        AuxiliarySurfaceTermination termination,
        string rationale)
    {
        return new AuxiliarySurfaceOwnershipEntry(
            $"window-field:{key}",
            AuxiliarySurfaceKind.Window,
            owner,
            termination,
            rationale);
    }

    private static AuxiliarySurfaceOwnershipEntry RuntimeWindow(string key, string rationale)
    {
        return new AuxiliarySurfaceOwnershipEntry(
            $"runtime-window:{key}",
            AuxiliarySurfaceKind.Window,
            AuxiliarySurfaceOwner.MiniPlayerRuntime,
            AuxiliarySurfaceTermination.RuntimeDispose,
            rationale);
    }

    private static AuxiliarySurfaceOwnershipEntry RuntimePopup(string key, string rationale)
    {
        return new AuxiliarySurfaceOwnershipEntry(
            $"runtime-popup:{key}",
            AuxiliarySurfaceKind.Popup,
            AuxiliarySurfaceOwner.MiniPlayerRuntime,
            AuxiliarySurfaceTermination.RuntimeDispose,
            rationale);
    }

    private static AuxiliarySurfaceOwnershipEntry PopupField(
        string key,
        AuxiliarySurfaceOwner owner,
        AuxiliarySurfaceTermination termination,
        string rationale)
    {
        return new AuxiliarySurfaceOwnershipEntry(
            $"popup-field:{key}",
            AuxiliarySurfaceKind.Popup,
            owner,
            termination,
            rationale);
    }

    private static AuxiliarySurfaceOwnershipEntry XamlPopup(string name)
    {
        return new AuxiliarySurfaceOwnershipEntry(
            $"xaml-popup:{name}",
            AuxiliarySurfaceKind.Popup,
            AuxiliarySurfaceOwner.XamlMainWindow,
            AuxiliarySurfaceTermination.ExplicitPopupClose,
            "MainWindow XAMLが生成と寿命を所有し、機能別の既存close/resetまたはWindow資源破棄で閉じる。");
    }
}
