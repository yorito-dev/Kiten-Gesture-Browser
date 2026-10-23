using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using TabBrowser.Models;
using TabBrowser.Services;
using TabBrowser.Views;

namespace TabBrowser;

public partial class MainWindow : IPageContextMenuHost, IPageContextMenuItemsHost, INativeWebContextMenuHost
{
    private const int TranslateSelectedTextMaxLength = 4500;
    private const int SearchSelectedTextMaxLength = 500;

    private readonly PageContextMenuController _pageContextMenuController;
    private BrowserTab? _pageContextMenuGestureSourceTab;
    private long? _pageContextMenuGestureInteractionId;
    private bool _suppressWorkspaceRightClickUntilRelease;
    private bool _suppressRightWheelContextMenuOnce;

    void IPageContextMenuHost.CopyContextPageUrl(BrowserTab sourceTab, string? pageUri)
        => CopyContextPageUrl(sourceTab, pageUri);

    void IPageContextMenuHost.OpenPageInYouTubeMiniPlayer(BrowserTab sourceTab)
        => _ = OpenTabInYouTubeMiniPlayerAsync(sourceTab);

    void IPageContextMenuHost.AddPageContainerContextMenu(ContextMenu menu, BrowserTab sourceTab, string pageUri)
        => AddPageContainerContextMenu(menu, sourceTab, pageUri);

    void IPageContextMenuHost.AddLinkContextMenuItems(ContextMenu menu, BrowserTab sourceTab, string linkUri, string? linkText, string? miniPlayerLinkUri)
        => AddLinkContextMenuItems(menu, sourceTab, linkUri, linkText, miniPlayerLinkUri);

    void IPageContextMenuHost.OpenCommandPaletteForLink(BrowserTab sourceTab, string linkUri, string? linkText)
        => OpenCommandPaletteForLink(sourceTab, linkUri, linkText);

    void IPageContextMenuHost.CopySelectedText(string? selectedText)
        => CopySelectedText(selectedText);

    void IPageContextMenuHost.QueueWebContextPagePaste(BrowserTab sourceTab, ContextMenu menu)
        => QueueWebContextPagePaste(sourceTab, menu);

    void IPageContextMenuHost.OpenContextMenuTranslation(BrowserTab sourceTab, string? selectedText, string? pageUrl)
        => OpenContextMenuTranslation(sourceTab, selectedText, pageUrl);

    bool IPageContextMenuHost.CanTranslateContextMenuTarget(string? selectedText, string? pageUrl)
        => CanTranslateContextMenuTarget(selectedText, pageUrl);

    void IPageContextMenuHost.QueueSearchSelectionCommand(BrowserTab sourceTab, string? selectedText)
        => QueueSearchSelectionCommand(sourceTab, selectedText);

    void IPageContextMenuHost.OpenSelectedTextXSearch(BrowserTab sourceTab, string? selectedText)
        => OpenSelectedTextXSearch(sourceTab, selectedText);

    void IPageContextMenuHost.OpenSelectedTextYouTubeSearch(BrowserTab sourceTab, string? selectedText)
        => OpenSelectedTextYouTubeSearch(sourceTab, selectedText);

    bool INativeWebContextMenuHost.HasWpfWebView(BrowserTab tab) => GetTabWebView(tab) is not null;

    bool INativeWebContextMenuHost.ShouldSuppressRightWheelContextMenu()
        => ShouldSuppressRightWheelContextMenu();

    string? INativeWebContextMenuHost.GetCurrentPageUrl(BrowserTab tab) => GetCurrentPageUrl(tab);

    bool INativeWebContextMenuHost.IsSplitViewLinkUrl(string? url) => IsSplitViewLinkUrl(url);

    bool INativeWebContextMenuHost.CanUseContextPageUrl(string? url) => CanUseContextPageUrl(url);

    string INativeWebContextMenuHost.NormalizeYouTubeShortsUrl(string url) => NormalizeYouTubeShortsUrl(url);

    void INativeWebContextMenuHost.ShowPageContextMenu(
        BrowserTab sourceTab,
        string? pageUri,
        string? linkUri,
        string? linkText,
        string? selectedText,
        bool isEditable,
        double? clientX,
        double? clientY)
        => ShowPageContextMenu(
            sourceTab,
            pageUri,
            linkUri,
            linkText,
            selectedText,
            isEditable,
            clientX,
            clientY);

    protected override void OnPreviewMouseRightButtonUp(MouseButtonEventArgs e)
    {
        EndRightWheelTabCycling();

        if (_suppressWorkspaceRightClickUntilRelease)
        {
            _suppressWorkspaceRightClickUntilRelease = false;
            _suppressRightWheelContextMenuOnce = true;
            e.Handled = true;
        }

        base.OnPreviewMouseRightButtonUp(e);
    }

    private void Window_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_suppressWorkspaceRightClickUntilRelease)
        {
            e.Handled = true;
            return;
        }

        if (ShouldSuppressRightWheelContextMenu())
        {
            e.Handled = true;
        }
    }

    private void SuppressWorkspaceRightClickIfWheelArrived(bool force = false)
    {
        SuppressNextPageContextMenu();
        if (ActiveTab is not { IsWorkspaceTab: true } || (!force && !IsRightMouseButtonDown()))
        {
            return;
        }

        _suppressWorkspaceRightClickUntilRelease = true;
    }

    private void SuppressNextPageContextMenu()
    {
        _suppressRightWheelContextMenuOnce = true;
    }

    private void ReleaseWorkspaceRightClickSuppressionAfterNativeRelease()
    {
        _suppressWorkspaceRightClickUntilRelease = false;
    }

    private void ResetRightClickSuppressionForNewPress()
    {
        _suppressWorkspaceRightClickUntilRelease = false;
        _suppressRightWheelContextMenuOnce = false;
    }

    private bool ShouldSuppressRightWheelContextMenu()
    {
        if (!_suppressRightWheelContextMenuOnce)
        {
            return false;
        }

        _suppressRightWheelContextMenuOnce = false;
        return true;
    }

    private void HandlePageContextMenuMessage(BrowserTab sourceTab, JsonElement root)
    {
        if (ShouldSuppressRightWheelContextMenu())
        {
            return;
        }

        ShowPageContextMenu(
            sourceTab,
            GetJsonString(root, "pageUrl"),
            GetJsonString(root, "linkUrl"),
            GetJsonString(root, "linkText"),
            GetJsonString(root, "selectedText"),
            GetJsonBool(root, "isEditable"),
            GetJsonDouble(root, "x"),
            GetJsonDouble(root, "y"));
    }

    private void ShowPageContextMenu(
        BrowserTab sourceTab,
        string? pageUri,
        string? linkUri,
        string? linkText,
        string? selectedText,
        bool isEditable,
        double? clientX,
        double? clientY)
    {
        if (!_tabs.Contains(sourceTab) || GetTabWebView(sourceTab) is not { } webView)
        {
            return;
        }

        var contextMenuPoint = new Point(Math.Max(0, clientX ?? 0), Math.Max(0, clientY ?? 0));
        _pageContextMenuController.Show(sourceTab, webView, contextMenuPoint, () =>
        {
            var targetPageUri = !string.IsNullOrWhiteSpace(pageUri)
                ? pageUri
                : GetCurrentPageUrl(sourceTab);
            var normalizedPageUri = CanUseContextPageUrl(targetPageUri)
                ? NormalizeYouTubeShortsUrl(targetPageUri!)
                : null;
            var normalizedLinkUri = IsSplitViewLinkUrl(linkUri)
                ? NormalizeYouTubeShortsUrl(linkUri!)
                : null;
            var miniPlayerLinkUri = IsSplitViewLinkUrl(linkUri)
                ? linkUri
                : null;
            return new PageContextMenuTarget(
                normalizedPageUri, normalizedLinkUri, miniPlayerLinkUri, linkText, selectedText, isEditable);
        });
    }

    private bool TryBeginPageContextMenuGesture(NativePoint screenPoint)
    {
        if (!_pageContextMenuController.TryGetOpenMenu(out var menu, out var sourceTab)
            || !_tabs.Contains(sourceTab)
            || GetTabWebView(sourceTab) is not { } webView
            || webView.CoreWebView2 is null)
        {
            return false;
        }

        var clientPoint = webView.PointFromScreen(new Point(screenPoint.X, screenPoint.Y));
        if (clientPoint.X < 0
            || clientPoint.Y < 0
            || clientPoint.X > webView.ActualWidth
            || clientPoint.Y > webView.ActualHeight)
        {
            return false;
        }

        var interaction = s_inputInteractionHub.TryBegin(
            InputInteractionKind.PageGesture,
            _workspaceWindowId,
            sourceTab.Id,
            DateTimeOffset.Now);
        if (!interaction.Accepted || interaction.Current is not { } currentInteraction)
        {
            return false;
        }

        _pageContextMenuGestureSourceTab = sourceTab;
        _pageContextMenuGestureInteractionId = currentInteraction.Id;
        menu.IsOpen = false;
        _ = StartPageContextMenuGestureAsync(sourceTab, clientPoint, currentInteraction.Id);
        return true;
    }

    private void TryFinishPageContextMenuGesture(NativePoint screenPoint)
    {
        if (_pageContextMenuGestureSourceTab is not { } sourceTab
            || _pageContextMenuGestureInteractionId is not { } interactionId)
        {
            return;
        }

        _pageContextMenuGestureSourceTab = null;
        _pageContextMenuGestureInteractionId = null;
        if (!_tabs.Contains(sourceTab)
            || GetTabWebView(sourceTab) is not { } webView
            || webView.CoreWebView2 is null)
        {
            return;
        }

        var clientPoint = webView.PointFromScreen(new Point(screenPoint.X, screenPoint.Y));
        _ = FinishPageContextMenuGestureAsync(sourceTab, clientPoint, interactionId);
    }

    private async Task StartPageContextMenuGestureAsync(
        BrowserTab sourceTab,
        Point clientPoint,
        long interactionId)
    {
        if (GetTabWebView(sourceTab)?.CoreWebView2 is not { } core
            || string.IsNullOrWhiteSpace(GetTabWebMessageToken(sourceTab)))
        {
            s_inputInteractionHub.TryComplete(interactionId);
            return;
        }

        var token = JsonSerializer.Serialize(GetTabWebMessageToken(sourceTab));
        var x = clientPoint.X.ToString(CultureInfo.InvariantCulture);
        var y = clientPoint.Y.ToString(CultureInfo.InvariantCulture);
        try
        {
            var result = await core.ExecuteScriptAsync($"window.__tabBrowserGestureHostStartAt?.({token}, {x}, {y});");
            if (!string.Equals(result, "true", StringComparison.Ordinal)
                || !s_inputInteractionHub.IsCurrent(interactionId))
            {
                if (string.Equals(result, "true", StringComparison.Ordinal))
                {
                    await CancelActiveGestureOverlayAsync(core);
                }

                s_inputInteractionHub.TryComplete(interactionId);
            }
        }
        catch
        {
            s_inputInteractionHub.TryComplete(interactionId);
            // ページ遷移中などで引き継げない場合は、通常の右クリック処理へフォールバックします。
        }
    }

    private async Task FinishPageContextMenuGestureAsync(
        BrowserTab sourceTab,
        Point clientPoint,
        long interactionId)
    {
        if (GetTabWebView(sourceTab)?.CoreWebView2 is not { } core
            || string.IsNullOrWhiteSpace(GetTabWebMessageToken(sourceTab)))
        {
            s_inputInteractionHub.TryComplete(interactionId);
            return;
        }

        var token = JsonSerializer.Serialize(GetTabWebMessageToken(sourceTab));
        var x = clientPoint.X.ToString(CultureInfo.InvariantCulture);
        var y = clientPoint.Y.ToString(CultureInfo.InvariantCulture);
        try
        {
            await core.ExecuteScriptAsync($"window.__tabBrowserGestureHostFinishAt?.({token}, {x}, {y});");
        }
        catch
        {
            // ページ遷移中などで完了通知できない場合は、次の入力で通常状態へ戻ります。
        }
        finally
        {
            s_inputInteractionHub.TryComplete(interactionId);
        }
    }

    private void CancelInputInteractionForTab(BrowserTab tab)
    {
        _pageContextMenuController.CloseForTab(tab);

        if (ReferenceEquals(_pageContextMenuGestureSourceTab, tab))
        {
            _pageContextMenuGestureSourceTab = null;
            _pageContextMenuGestureInteractionId = null;
        }

        ResetNativeWebGestureForTab(tab);
        s_inputInteractionHub.CancelForTab(_workspaceWindowId, tab.Id);
    }

    private void AddPageContainerContextMenu(ContextMenu menu, BrowserTab sourceTab, string pageUri)
        => PageContextMenuItemsBuilder.AddPageContainerContextMenu(this, menu, sourceTab, pageUri);

    private void AddLinkContextMenuItems(ContextMenu menu, BrowserTab sourceTab, string linkUri,
        string? linkText, string? miniPlayerLinkUri = null)
        => PageContextMenuItemsBuilder.AddLinkContextMenuItems(this, menu, sourceTab, linkUri, linkText, miniPlayerLinkUri);

    IReadOnlyList<ContainerProfile> IPageContextMenuItemsHost.ReloadContainerProfiles() => ReloadContainerProfiles();
    string IPageContextMenuItemsHost.GetContainerProfileMenuHeader(ContainerProfile profile) => GetContainerProfileMenuHeader(profile);
    object IPageContextMenuItemsHost.CreateContainerProfileMenuIcon(ContainerProfile profile) => CreateContainerProfileMenuIcon(profile);
    bool IPageContextMenuItemsHost.ContainsTab(BrowserTab tab) => _tabs.Contains(tab);
    BrowserTab IPageContextMenuItemsHost.OpenBackgroundTabNextTo(BrowserTab sourceTab, string url, string? title, string? containerProfileId)
        => OpenBackgroundTabNextTo(sourceTab, url, title: title, containerProfileId: containerProfileId);
    string IPageContextMenuItemsHost.GetCurrentPageTitle(BrowserTab tab, string url) => GetCurrentPageTitle(tab, url);
    void IPageContextMenuItemsHost.SelectTab(BrowserTab tab) => SelectTab(tab);
    void IPageContextMenuItemsHost.QueueSelectedTabActivation() => QueueSelectedTabActivation();
    void IPageContextMenuItemsHost.SetStatus(string message, bool autoClear) => SetStatus(message, autoClear);
    void IPageContextMenuItemsHost.OpenLinkInNewWindow(BrowserTab sourceTab, string linkUri, string? linkText)
        => OpenLinkInNewWindow(sourceTab, linkUri, linkText);
    void IPageContextMenuItemsHost.AddMiniPlayerLinkContextMenuItem(ContextMenu menu, BrowserTab sourceTab, string linkUri,
        string? linkText, string? miniPlayerLinkUri)
        => AddMiniPlayerLinkContextMenuItem(menu, sourceTab, linkUri, linkText, miniPlayerLinkUri);
    bool IPageContextMenuItemsHost.CanOpenInSplitView(BrowserTab tab) => CanOpenInSplitView(tab);
    bool IPageContextMenuItemsHost.CanReplaceSplitPaneWithLink(BrowserTab sourceTab, bool replaceOppositePane)
        => CanReplaceSplitPaneWithLink(sourceTab, replaceOppositePane);
    void IPageContextMenuItemsHost.QueueWebContextRightSplitOpen(BrowserTab sourceTab, string url)
        => QueueWebContextRightSplitOpen(sourceTab, url);
    void IPageContextMenuItemsHost.QueueWebContextBottomSplitOpen(BrowserTab sourceTab, string url)
        => QueueWebContextBottomSplitOpen(sourceTab, url);
    void IPageContextMenuItemsHost.QueueWebContextSplitPaneReplacement(BrowserTab sourceTab, string url, bool replaceOppositePane)
        => QueueWebContextSplitPaneReplacement(sourceTab, url, replaceOppositePane);
    void IPageContextMenuItemsHost.CopyContextLink(LinkCopyFormat format, string? linkUri, string? linkText, string successMessage)
        => CopyContextLink(format, linkUri, linkText, successMessage);

    private static MenuItem CreateContextCommand(string header, Action action, bool isEnabled = true)
        => PageContextMenuController.CreateContextCommand(header, action, isEnabled);

    private void QueueWebContextPagePaste(BrowserTab sourceTab, ContextMenu menu)
    {
        _windowTaskSupervisor.TryRun(
            "web-context-page-paste",
            cancellationToken => PasteIntoPageAsync(sourceTab, menu, cancellationToken));
    }

    private async Task PasteIntoPageAsync(
        BrowserTab sourceTab,
        ContextMenu menu,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        menu.IsOpen = false;

        try
        {
            await Dispatcher.InvokeAsync(
                static () => { },
                System.Windows.Threading.DispatcherPriority.ContextIdle);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (!IsActive
                || !_tabs.Contains(sourceTab)
                || GetTabWebView(sourceTab) is not { IsVisible: true } webView
                || webView.CoreWebView2 is null)
            {
                return;
            }

            webView.Focus();
            await Dispatcher.InvokeAsync(
                static () => { },
                System.Windows.Threading.DispatcherPriority.Input);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (!SendPagePasteShortcut())
            {
                SetStatus(
                    "貼り付けできませんでした。入力欄を選択してCtrl+Vを押してください。",
                    autoClear: true);
            }
        }
        catch (ObjectDisposedException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            SetStatus(
                "貼り付けできませんでした。入力欄を選択してCtrl+Vを押してください。",
                autoClear: true);
        }
        catch (InvalidOperationException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            SetStatus(
                "貼り付けできませんでした。入力欄を選択してCtrl+Vを押してください。",
                autoClear: true);
        }
    }

    private static bool SendPagePasteShortcut()
    {
        const ushort virtualKeyControl = 0x11;
        const ushort virtualKeyV = 0x56;
        const uint keyEventKeyUp = 0x0002;

        var inputs = new[]
        {
            CreateWebPasteKeyboardInput(virtualKeyControl, 0),
            CreateWebPasteKeyboardInput(virtualKeyV, 0),
            CreateWebPasteKeyboardInput(virtualKeyV, keyEventKeyUp),
            CreateWebPasteKeyboardInput(virtualKeyControl, keyEventKeyUp)
        };

        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<WebPasteInput>());
        if (sent == inputs.Length)
        {
            return true;
        }

        var releaseInputs = new[]
        {
            CreateWebPasteKeyboardInput(virtualKeyV, keyEventKeyUp),
            CreateWebPasteKeyboardInput(virtualKeyControl, keyEventKeyUp)
        };
        _ = SendInput((uint)releaseInputs.Length, releaseInputs, Marshal.SizeOf<WebPasteInput>());
        return false;
    }

    private static WebPasteInput CreateWebPasteKeyboardInput(ushort virtualKey, uint flags)
    {
        return new WebPasteInput
        {
            Type = 1,
            Data = new WebPasteInputUnion
            {
                Keyboard = new WebPasteKeyboardInput
                {
                    VirtualKey = virtualKey,
                    Flags = flags
                }
            }
        };
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, WebPasteInput[] inputs, int inputSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct WebPasteInput
    {
        public uint Type;
        public WebPasteInputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct WebPasteInputUnion
    {
        [FieldOffset(0)]
        public WebPasteMouseInput Mouse;

        [FieldOffset(0)]
        public WebPasteKeyboardInput Keyboard;

        [FieldOffset(0)]
        public WebPasteHardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WebPasteMouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WebPasteKeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WebPasteHardwareInput
    {
        public uint Message;
        public ushort ParameterLow;
        public ushort ParameterHigh;
    }

    private EventHandler<CoreWebView2ContextMenuRequestedEventArgs> CreateWpfWebContextMenuRequestedHandler(BrowserTab tab)
    {
        return (_, args) =>
        {
            if (!_tabs.Contains(tab))
            {
                return;
            }

            if (ShouldSuppressRightWheelContextMenu())
            {
                args.Handled = true;
                return;
            }

            var target = args.ContextMenuTarget;
            var selectionText = target.HasSelection
                ? target.SelectionText
                : null;
            var pageUri = !string.IsNullOrWhiteSpace(target.PageUri)
                ? target.PageUri
                : GetCurrentPageUrl(tab);
            var linkUri = !string.IsNullOrWhiteSpace(target.LinkUri)
                ? target.LinkUri
                : null;
            var linkText = !string.IsNullOrWhiteSpace(target.LinkText)
                ? target.LinkText
                : null;

            args.Handled = true;
            ShowPageContextMenu(
                tab,
                pageUri,
                linkUri,
                linkText,
                selectionText,
                target.IsEditable,
                args.Location.X,
                args.Location.Y);
        };
    }

    private EventHandler<CoreWebView2ContextMenuRequestedEventArgs> CreateWebContextMenuRequestedHandler(BrowserTab tab, CoreWebView2 core)
    {
        if (GetTabWebView(tab) is not null)
        {
            return CreateWpfWebContextMenuRequestedHandler(tab);
        }

        return NativeWebContextMenuController.CreateRequestedHandler(this, tab, core);
    }

    private void CopyContextLink(LinkCopyFormat format, string? linkUri, string? linkText, string successMessage)
    {
        if (!IsSplitViewLinkUrl(linkUri))
        {
            SetStatus("コピーできるリンクURLがありません", autoClear: true);
            return;
        }

        var target = new LinkCopyTarget(NormalizeYouTubeShortsUrl(linkUri!), linkText);
        SetLinkCopyStatus(_linkCopyService.Copy(target, format, successMessage));
    }

    private void CopySelectedText(string? selectedText)
    {
        SetLinkCopyStatus(_linkCopyService.CopyText(selectedText, "選択範囲をコピーしました"));
    }

    private void CopyContextPageUrl(BrowserTab sourceTab, string? pageUri)
    {
        if (!CanUseContextPageUrl(pageUri))
        {
            SetStatus("コピーできるページURLがありません", autoClear: true);
            return;
        }

        var normalizedPageUri = NormalizeYouTubeShortsUrl(pageUri!);
        var target = new LinkCopyTarget(normalizedPageUri, GetCurrentPageTitle(sourceTab, normalizedPageUri));
        SetLinkCopyStatus(_linkCopyService.Copy(target, LinkCopyFormat.Url, "URLをコピーしました"));
    }

    private void OpenLinkInNewWindow(BrowserTab sourceTab, string linkUri, string? linkText)
    {
        if (!IsSplitViewLinkUrl(linkUri))
        {
            SetStatus("新しいウィンドウで開けるリンクURLがありません", autoClear: true);
            return;
        }

        var initialTab = new SessionTabState
        {
            Url = NormalizeYouTubeShortsUrl(linkUri),
            Title = linkText,
            ContainerProfileId = sourceTab.ContainerProfileId
        };
        var position = new Point(Math.Max(0, Left + 32), Math.Max(0, Top + 32));
        var newWindow = new MainWindow(initialTab, position, _isPrivateWindow);
        newWindow.Show();
        newWindow.Activate();
        SetStatus("リンクを新しいウィンドウで開きました", autoClear: true);
    }

    private void QueueWebContextRightSplitOpen(BrowserTab sourceTab, string url)
    {
        _windowTaskSupervisor.TryRun(
            "web-context-link-right-split-open",
            cancellationToken => OpenLinkInRightSplitAsync(sourceTab, url, cancellationToken));
    }

    private void QueueWebContextBottomSplitOpen(BrowserTab sourceTab, string url)
    {
        _windowTaskSupervisor.TryRun(
            "web-context-link-bottom-split-open",
            cancellationToken => OpenLinkInBottomSplitAsync(sourceTab, url, cancellationToken));
    }

    private async Task OpenLinkInRightSplitAsync(
        BrowserTab sourceTab,
        string url,
        CancellationToken cancellationToken = default)
    {
        await OpenLinkInSplitAsync(sourceTab, url, SplitViewOrientation.LeftRight, cancellationToken);
    }

    private async Task OpenLinkInBottomSplitAsync(
        BrowserTab sourceTab,
        string url,
        CancellationToken cancellationToken = default)
    {
        await OpenLinkInSplitAsync(sourceTab, url, SplitViewOrientation.TopBottom, cancellationToken);
    }

    private async Task OpenLinkInSplitAsync(
        BrowserTab sourceTab,
        string url,
        string orientation,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (!CanOpenInSplitView(sourceTab) || !IsSplitViewLinkUrl(url))
        {
            SetStatus("このリンクは分割表示できません", autoClear: true);
            return;
        }

        var rightTab = OpenBackgroundTabNextTo(
            sourceTab,
            NormalizeYouTubeShortsUrl(url),
            queueBackgroundLoading: false);
        await StartSplitViewAsync(sourceTab, rightTab, orientation, cancellationToken);
    }

    private bool CanReplaceSplitPaneWithLink(BrowserTab sourceTab, bool replaceOppositePane)
    {
        return TryGetSplitPaneReplacementTarget(sourceTab, replaceOppositePane, out _);
    }

    private bool TryGetSplitPaneReplacementTarget(
        BrowserTab sourceTab,
        bool replaceOppositePane,
        out BrowserTab? targetTab)
    {
        targetTab = null;
        if (!_tabs.Contains(sourceTab)
            || sourceTab.IsNativeContentTab
            || !_splitViewService.TryGetPane(sourceTab, out var sourcePane))
        {
            return false;
        }

        var targetPane = replaceOppositePane
            ? sourcePane == SplitViewPane.Left
                ? SplitViewPane.Right
                : SplitViewPane.Left
            : sourcePane;
        targetTab = _splitViewService.GetTabForPane(_tabs, targetPane);
        return targetTab is { IsNativeContentTab: false };
    }

    private void QueueWebContextSplitPaneReplacement(
        BrowserTab sourceTab,
        string url,
        bool replaceOppositePane)
    {
        _windowTaskSupervisor.TryRun(
            "web-context-split-pane-replace",
            cancellationToken => ReplaceSplitPaneWithLinkAsync(
                sourceTab,
                url,
                replaceOppositePane,
                cancellationToken));
    }

    private async Task ReplaceSplitPaneWithLinkAsync(
        BrowserTab sourceTab,
        string url,
        bool replaceOppositePane,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (!IsSplitViewLinkUrl(url)
            || !TryGetSplitPaneReplacementTarget(sourceTab, replaceOppositePane, out var targetTab)
            || targetTab is null)
        {
            SetStatus("このリンクで置き換えられる分割ペインがありません", autoClear: true);
            return;
        }

        try
        {
            var activeSplitTab = _splitViewService.GetActiveTab(_tabs);
            var updateAddressText = ReferenceEquals(activeSplitTab, targetTab);
            await NavigateTabToUrlAsync(
                targetTab,
                url,
                updateAddressText: updateAddressText,
                cancellationToken: cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            SetStatus(
                replaceOppositePane
                    ? "反対側ペインをリンク先に置き換えました"
                    : "このペインをリンク先に置き換えました",
                autoClear: true);
        }
        catch (Exception ex)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            SetStatus("分割ペインの置き換えに失敗しました: " + ex.Message, autoClear: true);
        }
    }

    private static bool IsSearchSelectedTextShortcut(Key key, ModifierKeys modifiers)
    {
        return key == Key.S && modifiers == (ModifierKeys.Control | ModifierKeys.Shift);
    }

    private void QueueActivePageSelectionSearch()
    {
        _windowTaskSupervisor.TryRun(
            "search-active-page-selection",
            SearchActivePageSelectionAsync);
    }

    private async Task SearchActivePageSelectionAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (ActiveTab is not { IsWorkspaceTab: false } tab
            || GetTabWebView(tab)?.CoreWebView2 is not { } core)
        {
            SetStatus("選択テキストを検索できるページがありません", autoClear: true);
            return;
        }

        try
        {
            var selectionJson = await core.ExecuteScriptAsync(
                "(() => { const selection = window.getSelection(); return selection ? selection.toString() : ''; })();");
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var selectedText = JsonSerializer.Deserialize<string>(selectionJson);
            await ExecuteSearchSelectionCommandAsync(tab, selectedText, cancellationToken);
        }
        catch (Exception ex)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            SetStatus("選択テキストの取得に失敗しました: " + ex.Message, autoClear: true);
        }
    }

    private void OpenSelectedTextSearch(BrowserTab? sourceTab, string? selectedText)
    {
        var query = selectedText?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            SetStatus("選択テキストがありません", autoClear: true);
            return;
        }

        if (query.Length > SearchSelectedTextMaxLength)
        {
            query = query[..SearchSelectedTextMaxLength];
        }

        var searchUrl = _addressService.CreateSearchUrl(query);
        var openedTab = sourceTab is not null && _tabs.Contains(sourceTab)
            ? OpenBackgroundTabNextTo(sourceTab, searchUrl, title: query)
            : OpenTab(searchUrl, activate: false, title: query);
        SelectTab(openedTab);
        QueueSelectedTabActivation();
        SetStatus("選択テキストを検索しました", autoClear: true);
    }

    private void OpenContextMenuTranslation(BrowserTab sourceTab, string? selectedText, string? pageUrl)
    {
        var selection = selectedText?.Trim();
        if (!string.IsNullOrWhiteSpace(selection))
        {
            if (selection.Length > TranslateSelectedTextMaxLength)
            {
                selection = selection[..TranslateSelectedTextMaxLength];
            }

            var translateUrl = "https://translate.google.com/?sl=auto&tl=ja&text="
                + Uri.EscapeDataString(selection)
                + "&op=translate";
            OpenBackgroundTabNextTo(sourceTab, translateUrl, title: "翻訳");
            SetStatus("選択テキストを裏タブで翻訳しました", autoClear: true);
            return;
        }

        var targetUrl = !string.IsNullOrWhiteSpace(pageUrl)
            ? pageUrl
            : GetCurrentPageUrl(sourceTab);
        if (string.IsNullOrWhiteSpace(targetUrl) || !IsTranslatablePageUrl(targetUrl))
        {
            SetStatus("翻訳できるページではありません", autoClear: true);
            return;
        }

        var pageTranslateUrl = "https://translate.google.com/translate?sl=auto&tl=ja&u="
            + Uri.EscapeDataString(targetUrl);
        OpenBackgroundTabNextTo(sourceTab, pageTranslateUrl, title: "ページ翻訳");
        SetStatus("ページ翻訳を裏タブで開きました", autoClear: true);
    }

    private void OpenSelectedTextXSearch(BrowserTab sourceTab, string? selectedText)
    {
        OpenSelectedTextSiteSearch(
            sourceTab,
            selectedText,
            "https://x.com/search?q={0}&src=typed_query",
            "X 検索",
            "選択テキストを X の裏タブで検索しました");
    }

    private void OpenSelectedTextYouTubeSearch(BrowserTab sourceTab, string? selectedText)
    {
        OpenSelectedTextSiteSearch(
            sourceTab,
            selectedText,
            "https://www.youtube.com/results?search_query={0}",
            "YouTube 検索",
            "選択テキストを YouTube の裏タブで検索しました");
    }

    private void OpenSelectedTextSiteSearch(
        BrowserTab sourceTab,
        string? selectedText,
        string urlTemplate,
        string title,
        string statusMessage)
    {
        var query = selectedText?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            SetStatus("選択テキストがありません", autoClear: true);
            return;
        }

        if (query.Length > SearchSelectedTextMaxLength)
        {
            query = query[..SearchSelectedTextMaxLength];
        }

        var searchUrl = string.Format(CultureInfo.InvariantCulture, urlTemplate, Uri.EscapeDataString(query));
        OpenBackgroundTabNextTo(sourceTab, searchUrl, title: title);
        SetStatus(statusMessage, autoClear: true);
    }

    private static bool CanTranslateContextMenuTarget(string? selectedText, string? pageUrl)
    {
        return !string.IsNullOrWhiteSpace(selectedText)
            || IsTranslatablePageUrl(pageUrl);
    }

    private static bool IsTranslatablePageUrl(string? url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }
}
