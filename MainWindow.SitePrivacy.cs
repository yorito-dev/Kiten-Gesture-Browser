using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using TabBrowser.Models;
using TabBrowser.Services;

namespace TabBrowser;

public partial class MainWindow
{
    private readonly Dictionary<Guid, List<SitePermissionPromptWindow>> _sitePermissionPrompts = [];
    private readonly SitePrivacyInteractionCoordinator _sitePrivacyInteractionCoordinator = new();
    private Point _siteInformationDragStartPoint;
    private bool _isSiteInformationDragCandidate;
    private bool _suppressSiteInformationClick;

    private async void SitePrivacyMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await OpenSitePrivacyWindowAsync(null, null);
    }

    private async Task OpenSitePrivacyWindowAsync(string? containerProfileId, string? origin)
    {
        if (ModelessWindowService.TryGetOwned<SitePrivacyWindow>(this, out var existing))
        {
            if (containerProfileId is not null || origin is not null)
            {
                existing.FocusContext(containerProfileId, origin);
            }
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            existing.Activate();
            return;
        }

        var containers = _isPrivateWindow
            ? new List<ContainerProfile>
            {
                new()
                {
                    Id = ContainerProfile.DefaultId,
                    Name = "プライベートWindow",
                    IsDefault = true
                }
            }
            : ReloadContainerProfiles(applyToTabs: false).ToList();
        var window = new SitePrivacyWindow(
            containers,
            EnsureWebViewEnvironmentAsync,
            ClearApplicationSiteDataAsync,
            GetPopupPermissionEntries,
            UpdatePopupPolicyFromManager,
            containerProfileId,
            origin)
        {
            Owner = this
        };
        await ModelessWindowService.ShowAsync(window);
    }

    private async void SiteInformationButton_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressSiteInformationClick)
        {
            _suppressSiteInformationClick = false;
            return;
        }

        if (SiteInformationPopup.IsOpen)
        {
            CloseSiteInformationPopup();
            return;
        }

        await ShowSiteInformationAsync();
    }

    private void SiteInformationButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _suppressSiteInformationClick = false;
        _siteInformationDragStartPoint = e.GetPosition(SiteInformationButton);
        _isSiteInformationDragCandidate = TryGetSiteInformationTarget(out _, out _);
    }

    private void SiteInformationButton_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isSiteInformationDragCandidate = false;
    }

    private void SiteInformationButton_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isSiteInformationDragCandidate || e.LeftButton != MouseButtonState.Pressed)
        {
            _isSiteInformationDragCandidate = false;
            return;
        }

        var currentPoint = e.GetPosition(SiteInformationButton);
        if (Math.Abs(currentPoint.X - _siteInformationDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(currentPoint.Y - _siteInformationDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (!TryGetSiteInformationTarget(out var tab, out var information))
        {
            _isSiteInformationDragCandidate = false;
            return;
        }

        _isSiteInformationDragCandidate = false;
        _suppressSiteInformationClick = true;
        CloseSiteInformationPopup();
        e.Handled = true;
        DragDrop.DoDragDrop(
            SiteInformationButton,
            CreateSiteInformationUrlDragData(tab, information.Url),
            DragDropEffects.Copy);
    }

    private async Task ShowSiteInformationAsync()
    {
        if (!TryGetSiteInformationTarget(out var tab, out var information))
        {
            return;
        }

        var generation = _sitePrivacyInteractionCoordinator.BeginSiteInformation(
            tab.Id,
            information);
        var profile = GetContainerProfile(tab.ContainerProfileId);
        SiteInformationHostText.Text = information.Host;
        SiteInformationOriginText.Text = information.Origin;
        SiteInformationConnectionText.Text = information.ConnectionName;
        SiteInformationContainerText.Text = $"コンテナ: {profile.Name}";
        SiteInformationContainerColor.Background = tab.ContainerProfileBrush;
        SiteInformationManageButton.IsEnabled = information.SupportsSitePermissions;
        SiteInformationPermissionText.Text = information.SupportsSitePermissions
            ? "保存済みの権限を読み込んでいます..."
            : "このページにサイト権限はありません";
        SiteInformationPopup.IsOpen = true;

        if (!information.SupportsSitePermissions)
        {
            return;
        }

        IReadOnlyList<SitePermissionEntry> permissions = [];
        try
        {
            if (GetTabWebView(tab)?.CoreWebView2?.Profile is { } webViewProfile)
            {
                var entries = await SitePrivacyService.GetPermissionEntriesAsync(webViewProfile);
                permissions = SitePrivacyService.GetEntriesForOrigin(entries, information.Origin);
            }
        }
        catch
        {
            if (_sitePrivacyInteractionCoordinator.IsCurrentSiteInformationGeneration(generation)
                && SiteInformationPopup.IsOpen)
            {
                SiteInformationPermissionText.Text = "保存済みの権限情報を読み込めませんでした";
            }

            return;
        }

        if (!_sitePrivacyInteractionCoordinator.IsCurrentSiteInformation(
                generation,
                tab.Id,
                information.Url)
            || !SiteInformationPopup.IsOpen)
        {
            return;
        }

        var lines = permissions
            .Select(static permission => $"{permission.PermissionName}: {permission.StateName}")
            .ToList();
        var popupPolicy = GetPopupPolicy(information.Url, tab.ContainerProfileId);
        lines.Add($"ポップアップ: {(popupPolicy is null ? "既定" : GetPopupPolicyName(popupPolicy))}");
        SiteInformationPermissionText.Text = string.Join(Environment.NewLine, lines);
    }

    private void CopySiteInformationUrlButton_Click(object sender, RoutedEventArgs e)
    {
        var context = _sitePrivacyInteractionCoordinator.CaptureSiteInformationContext();
        if (context.Information is not { } information)
        {
            return;
        }

        try
        {
            Clipboard.SetText(information.Url);
            SetStatus("URLをコピーしました", autoClear: true);
        }
        catch
        {
            SetStatus("URLをコピーできませんでした", autoClear: true);
        }
    }

    private async void OpenSitePrivacyFromInformationButton_Click(object sender, RoutedEventArgs e)
    {
        var context = _sitePrivacyInteractionCoordinator.CaptureSiteInformationContext();
        if (context.Information is not { SupportsSitePermissions: true } information
            || context.TabId is not { } tabId
            || _tabs.FirstOrDefault(tab => tab.Id == tabId) is not { } tab)
        {
            return;
        }

        CloseSiteInformationPopup();
        await OpenSitePrivacyWindowAsync(tab.ContainerProfileId, information.Origin);
    }

    private void SiteInformationPopup_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        CloseSiteInformationPopup();
        e.Handled = true;
    }

    private void UpdateSiteInformationButtonState()
    {
        var hasTarget = TryGetSiteInformationTarget(out var tab, out var information);
        SiteInformationButton.IsEnabled = hasTarget;
        SiteInformationButton.ToolTip = hasTarget
            ? $"{information.Host} のサイト情報（ドラッグでURLを渡せます）"
            : "このタブにはサイト情報がありません";

        if (SiteInformationPopup.IsOpen
            && (!hasTarget
                || !_sitePrivacyInteractionCoordinator.IsCurrentSiteInformationContext(
                    tab.Id,
                    information.Url)))
        {
            CloseSiteInformationPopup();
        }
    }

    private bool TryGetSiteInformationTarget(
        out BrowserTab tab,
        out SiteAddressInformation information)
    {
        tab = GetActiveBrowserCommandTarget()!;
        if (tab is null || tab.IsNativeContentTab)
        {
            information = default!;
            return false;
        }

        var url = GetCurrentPageUrl(tab) ?? tab.Url;
        return SitePrivacyService.TryCreateAddressInformation(url, out information);
    }

    private void CloseSiteInformationPopup()
    {
        _sitePrivacyInteractionCoordinator.CloseSiteInformation();
        SiteInformationPopup.IsOpen = false;
    }

    private void NewPrivateWindowMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var window = new MainWindow(
            initialTabs: null,
            persistSession: false,
            initialPosition: new Point(Left + 36, Top + 36),
            isPrivateWindow: true);
        window.Show();
    }

    private async Task HandleSitePermissionRequestedAsync(
        BrowserTab tab,
        CoreWebView2PermissionRequestedEventArgs args,
        CoreWebView2Deferral deferral)
    {
        try
        {
            var decision = await ShowSitePermissionPromptAsync(
                tab,
                args.Uri,
                SitePrivacyService.GetPermissionName(args.PermissionKind),
                isScreenCapture: false);
            if (!_tabs.Contains(tab))
            {
                decision = SitePermissionDecision.Dismiss;
            }

            args.State = decision switch
            {
                SitePermissionDecision.AllowOnce or SitePermissionDecision.AllowAlways =>
                    CoreWebView2PermissionState.Allow,
                _ => CoreWebView2PermissionState.Deny
            };
            args.SavesInProfile = decision is SitePermissionDecision.AllowAlways
                or SitePermissionDecision.Deny;
        }
        catch
        {
            args.State = CoreWebView2PermissionState.Deny;
            args.SavesInProfile = false;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async Task HandleScreenCaptureStartingAsync(
        BrowserTab tab,
        CoreWebView2ScreenCaptureStartingEventArgs args,
        CoreWebView2Deferral deferral)
    {
        try
        {
            var source = args.OriginalSourceFrameInfo?.Source ?? GetCurrentPageUrl(tab) ?? tab.Url;
            var decision = await ShowSitePermissionPromptAsync(
                tab,
                source,
                "画面共有",
                isScreenCapture: true);
            args.Cancel = !_tabs.Contains(tab)
                || decision is not (SitePermissionDecision.AllowOnce or SitePermissionDecision.AllowAlways);
        }
        catch
        {
            args.Cancel = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async Task<SitePermissionDecision> ShowSitePermissionPromptAsync(
        BrowserTab tab,
        string? requestUrl,
        string permissionName,
        bool isScreenCapture)
    {
        var origin = PageZoomPreferenceSettingsProfile.TryNormalizeOrigin(requestUrl, out var normalizedOrigin)
            ? normalizedOrigin
            : requestUrl ?? "不明なサイト";
        var containerName = _isPrivateWindow
            ? "プライベートWindow"
            : GetContainerProfile(tab.ContainerProfileId).Name;
        var request = new SitePermissionRequestInfo(
            tab.Id,
            tab.DisplayTitle,
            origin,
            tab.ContainerProfileId,
            containerName,
            permissionName,
            isScreenCapture,
            _isPrivateWindow);
        var window = new SitePermissionPromptWindow(request)
        {
            Owner = this
        };
        WindowTopmostService.Apply(window, Topmost);
        if (!_sitePermissionPrompts.TryGetValue(tab.Id, out var prompts))
        {
            prompts = [];
            _sitePermissionPrompts[tab.Id] = prompts;
        }

        prompts.Add(window);
        window.Show();
        try
        {
            return await window.Completion;
        }
        finally
        {
            prompts.Remove(window);
            if (prompts.Count == 0)
            {
                _sitePermissionPrompts.Remove(tab.Id);
            }
        }
    }

    private void CancelSitePermissionPromptsForTab(Guid tabId)
    {
        if (!_sitePermissionPrompts.Remove(tabId, out var prompts))
        {
            return;
        }

        foreach (var prompt in prompts.ToList())
        {
            prompt.CancelForTabClose();
        }
    }

    private void CancelAllSitePermissionPrompts()
    {
        foreach (var tabId in _sitePermissionPrompts.Keys.ToList())
        {
            CancelSitePermissionPromptsForTab(tabId);
        }
    }

    private bool HandleAutomaticPopup(BrowserTab tab, string? popupUrl)
    {
        if (string.IsNullOrWhiteSpace(popupUrl))
        {
            return true;
        }

        var sourceUrl = GetCurrentPageUrl(tab) ?? tab.Url ?? string.Empty;
        var policy = GetPopupPolicy(sourceUrl, tab.ContainerProfileId);
        if (string.Equals(policy, PopupPermissionPolicy.Allow, StringComparison.Ordinal))
        {
            OpenBackgroundTabNextTo(tab, popupUrl);
            SetStatus("許可済みサイトのポップアップを裏タブで開きました", autoClear: true);
            return true;
        }

        if (string.Equals(policy, PopupPermissionPolicy.Block, StringComparison.Ordinal))
        {
            SetStatus("このサイトのポップアップをブロックしました", autoClear: true);
            return true;
        }

        _sitePrivacyInteractionCoordinator.SetPendingBlockedPopup(tab.Id, sourceUrl, popupUrl);
        BlockedPopupText.Text = $"{tab.DisplayTitle} のポップアップをブロックしました";
        BlockedPopupNotice.Visibility = Visibility.Visible;
        return true;
    }

    private string? GetPopupPolicy(string sourceUrl, string containerProfileId)
    {
        if (!_isPrivateWindow)
        {
            return SitePrivacySettingsProfile.GetPopupPolicy(
                _settings.SitePrivacy,
                sourceUrl,
                containerProfileId);
        }

        return _sitePrivacyInteractionCoordinator.GetPrivatePopupPolicy(sourceUrl);
    }

    private void RecordPopupPolicy(string sourceUrl, string containerProfileId, string policy)
    {
        if (_isPrivateWindow)
        {
            _sitePrivacyInteractionCoordinator.SetPrivatePopupPolicy(sourceUrl, policy);
            return;
        }

        _settings.SitePrivacy = SitePrivacySettingsProfile.RecordPopupPolicy(
            _settings.SitePrivacy,
            sourceUrl,
            containerProfileId,
            policy,
            DateTimeOffset.UtcNow);
        SettingsService.Save(_settings);
    }

    private IReadOnlyList<PopupPermissionEntry> GetPopupPermissionEntries(string containerProfileId)
    {
        if (_isPrivateWindow)
        {
            return _sitePrivacyInteractionCoordinator.CapturePrivatePopupPolicies()
                .Select(static entry => new PopupPermissionEntry(
                    entry.Origin,
                    entry.Policy,
                    GetPopupPolicyName(entry.Policy)))
                .ToList();
        }

        var normalizedContainerId = ContainerProfile.NormalizeId(containerProfileId);
        return SitePrivacySettingsProfile.Normalize(_settings.SitePrivacy)
            .PopupRules
            .Where(rule => rule.ContainerProfileId.Equals(
                normalizedContainerId,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(static rule => rule.Origin, StringComparer.OrdinalIgnoreCase)
            .Select(static rule => new PopupPermissionEntry(
                rule.Origin,
                rule.Policy,
                GetPopupPolicyName(rule.Policy)))
            .ToList();
    }

    private void UpdatePopupPolicyFromManager(
        string containerProfileId,
        string origin,
        string? policy)
    {
        if (_isPrivateWindow)
        {
            if (policy is null)
            {
                _sitePrivacyInteractionCoordinator.RemovePrivatePopupPolicy(origin);
            }
            else
            {
                _sitePrivacyInteractionCoordinator.SetPrivatePopupPolicy(origin, policy);
            }

            return;
        }

        _settings.SitePrivacy = policy is null
            ? SitePrivacySettingsProfile.RemovePopupPolicy(
                _settings.SitePrivacy,
                origin,
                containerProfileId)
            : SitePrivacySettingsProfile.RecordPopupPolicy(
                _settings.SitePrivacy,
                origin,
                containerProfileId,
                policy,
                DateTimeOffset.UtcNow);
        SettingsService.Save(_settings);
    }

    private static string GetPopupPolicyName(string policy)
    {
        return string.Equals(policy, PopupPermissionPolicy.Allow, StringComparison.Ordinal)
            ? "許可"
            : "ブロック";
    }

    private void OpenBlockedPopupOnceButton_Click(object sender, RoutedEventArgs e)
    {
        var pending = TakePendingBlockedPopup();
        if (pending is not null
            && _tabs.FirstOrDefault(tab => tab.Id == pending.TabId) is { } tab)
        {
            OpenBackgroundTabNextTo(tab, pending.PopupUrl);
        }
    }

    private void AlwaysAllowBlockedPopupButton_Click(object sender, RoutedEventArgs e)
    {
        var pending = TakePendingBlockedPopup();
        if (pending is null
            || _tabs.FirstOrDefault(tab => tab.Id == pending.TabId) is not { } tab)
        {
            return;
        }

        RecordPopupPolicy(
            pending.SourceUrl,
            tab.ContainerProfileId,
            PopupPermissionPolicy.Allow);
        OpenBackgroundTabNextTo(tab, pending.PopupUrl);
    }

    private void BlockPopupButton_Click(object sender, RoutedEventArgs e)
    {
        var pending = TakePendingBlockedPopup();
        if (pending is null
            || _tabs.FirstOrDefault(tab => tab.Id == pending.TabId) is not { } tab)
        {
            return;
        }

        RecordPopupPolicy(
            pending.SourceUrl,
            tab.ContainerProfileId,
            PopupPermissionPolicy.Block);
    }

    private void CloseBlockedPopupNoticeButton_Click(object sender, RoutedEventArgs e)
    {
        TakePendingBlockedPopup();
    }

    private BlockedPopupRequest? TakePendingBlockedPopup()
    {
        var pending = _sitePrivacyInteractionCoordinator.TakePendingBlockedPopup();
        BlockedPopupNotice.Visibility = Visibility.Collapsed;
        return pending;
    }

    private async Task<SiteDataClearResult> ClearApplicationSiteDataAsync(SiteDataClearRequest request)
    {
        var browsingHistoryDeleted = 0;
        if (!_isPrivateWindow && request.Categories.HasFlag(SiteDataCategory.BrowsingHistory))
        {
            browsingHistoryDeleted = await App.BrowsingHistory.DeleteContainerAsync(
                request.ContainerProfileId,
                request.From,
                request.To);
        }

        var downloadHistoryDeleted = request.Categories.HasFlag(SiteDataCategory.DownloadHistory)
            ? _downloadService.DeleteHistory(
                request.ContainerProfileId,
                request.From,
                request.To)
            : 0;
        return new SiteDataClearResult(browsingHistoryDeleted, downloadHistoryDeleted);
    }
}
