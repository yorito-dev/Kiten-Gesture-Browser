using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using TabBrowser.Models;
using TabBrowser.Services;

namespace TabBrowser;

public partial class SitePrivacyWindow : Window
{
    private readonly Func<string, Task<CoreWebView2Environment>> _ensureEnvironmentAsync;
    private readonly Func<SiteDataClearRequest, Task<SiteDataClearResult>> _clearApplicationDataAsync;
    private readonly Func<string, IReadOnlyList<PopupPermissionEntry>> _getPopupEntries;
    private readonly Action<string, string, string?> _updatePopupPolicy;
    private WebView2? _profileWebView;
    private CoreWebView2Profile? _profile;
    private SiteDataClearRequest? _pendingClearRequest;
    private string? _focusedOrigin;
    private int _profileLoadGeneration;
    private bool _closed;

    public SitePrivacyWindow(
        IReadOnlyList<ContainerProfile> containers,
        Func<string, Task<CoreWebView2Environment>> ensureEnvironmentAsync,
        Func<SiteDataClearRequest, Task<SiteDataClearResult>> clearApplicationDataAsync,
        Func<string, IReadOnlyList<PopupPermissionEntry>> getPopupEntries,
        Action<string, string, string?> updatePopupPolicy,
        string? initialContainerProfileId = null,
        string? initialOrigin = null)
    {
        InitializeComponent();
        _ensureEnvironmentAsync = ensureEnvironmentAsync;
        _clearApplicationDataAsync = clearApplicationDataAsync;
        _getPopupEntries = getPopupEntries;
        _updatePopupPolicy = updatePopupPolicy;
        ContainerBox.ItemsSource = containers;
        _focusedOrigin = NormalizeFocusedOrigin(initialOrigin);
        ContainerBox.SelectedItem = containers.FirstOrDefault(container => string.Equals(
                container.Id,
                ContainerProfile.NormalizeId(initialContainerProfileId),
                StringComparison.OrdinalIgnoreCase))
            ?? containers.FirstOrDefault();
        PeriodBox.ItemsSource = new[]
        {
            new PeriodOption("全期間", "all"),
            new PeriodOption("今日", "today"),
            new PeriodOption("過去7日", "7days"),
            new PeriodOption("過去30日", "30days")
        };
        PeriodBox.SelectedIndex = 0;
    }

    public void FocusContext(string? containerProfileId, string? origin)
    {
        _focusedOrigin = NormalizeFocusedOrigin(origin);
        var normalizedContainerId = ContainerProfile.NormalizeId(containerProfileId);
        var target = (ContainerBox.ItemsSource as IEnumerable<ContainerProfile> ?? [])
            .FirstOrDefault(container => container.Id.Equals(
                normalizedContainerId,
                StringComparison.OrdinalIgnoreCase));
        if (target is not null && !ReferenceEquals(ContainerBox.SelectedItem, target))
        {
            ContainerBox.SelectedItem = target;
            return;
        }

        if (ContainerBox.SelectedItem is ContainerProfile selected)
        {
            ReloadPopupEntries(selected.Id);
        }

        SelectFocusedEntries();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _profileLoadGeneration++;
        DisposeProfileWebView();
        base.OnClosed(e);
    }

    private async void ContainerBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await ReloadProfileAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await ReloadProfileAsync();
    }

    private async Task ReloadProfileAsync()
    {
        if (_closed || ContainerBox.SelectedItem is not ContainerProfile container)
        {
            return;
        }

        var generation = ++_profileLoadGeneration;
        ReloadPopupEntries(container.Id);
        StatusText.Text = $"{container.Name}の権限を読み込んでいます...";
        PermissionGrid.ItemsSource = null;
        _profile = null;
        DisposeProfileWebView();

        try
        {
            var environment = await _ensureEnvironmentAsync(container.Id);
            if (_closed || generation != _profileLoadGeneration)
            {
                return;
            }

            var webView = new WebView2
            {
                Width = 1,
                Height = 1,
                IsHitTestVisible = false
            };
            _profileWebView = webView;
            ProfileWebViewHost.Children.Add(webView);
            await webView.EnsureCoreWebView2Async(environment);
            if (_closed || generation != _profileLoadGeneration)
            {
                return;
            }

            _profile = webView.CoreWebView2.Profile;
            await ReloadPermissionEntriesAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = "サイト権限を読み込めません: " + ex.Message;
        }
    }

    private async Task ReloadPermissionEntriesAsync()
    {
        if (_profile is null)
        {
            return;
        }

        var entries = await SitePrivacyService.GetPermissionEntriesAsync(_profile);
        PermissionGrid.ItemsSource = entries;
        StatusText.Text = entries.Count == 0
            ? "個別に保存されたサイト権限はありません"
            : $"{entries.Count}件の権限を表示";
        SelectFocusedEntries();
        UpdatePermissionButtons();
    }

    private void PermissionGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdatePermissionButtons();
    }

    private void PopupRuleGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdatePopupButtons();
    }

    private void AllowPopupButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateSelectedPopupPolicy(PopupPermissionPolicy.Allow);
    }

    private void BlockPopupRuleButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateSelectedPopupPolicy(PopupPermissionPolicy.Block);
    }

    private void ResetPopupRuleButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateSelectedPopupPolicy(null);
    }

    private void UpdateSelectedPopupPolicy(string? policy)
    {
        if (ContainerBox.SelectedItem is not ContainerProfile container
            || PopupRuleGrid.SelectedItem is not PopupPermissionEntry entry)
        {
            return;
        }

        _updatePopupPolicy(container.Id, entry.Origin, policy);
        ReloadPopupEntries(container.Id);
        StatusText.Text = policy is null
            ? "ポップアップ設定を既定へ戻しました"
            : "ポップアップ設定を変更しました";
    }

    private void ReloadPopupEntries(string containerProfileId)
    {
        PopupRuleGrid.ItemsSource = _getPopupEntries(containerProfileId);
        SelectFocusedEntries();
        UpdatePopupButtons();
    }

    private void SelectFocusedEntries()
    {
        if (string.IsNullOrWhiteSpace(_focusedOrigin))
        {
            return;
        }

        if ((PermissionGrid.ItemsSource as IEnumerable<SitePermissionEntry>)?.FirstOrDefault(entry =>
                entry.Origin.Equals(_focusedOrigin, StringComparison.OrdinalIgnoreCase)) is { } permission)
        {
            PermissionGrid.SelectedItem = permission;
            PermissionGrid.ScrollIntoView(permission);
        }

        if ((PopupRuleGrid.ItemsSource as IEnumerable<PopupPermissionEntry>)?.FirstOrDefault(entry =>
                entry.Origin.Equals(_focusedOrigin, StringComparison.OrdinalIgnoreCase)) is { } popup)
        {
            PopupRuleGrid.SelectedItem = popup;
            PopupRuleGrid.ScrollIntoView(popup);
        }
    }

    private static string? NormalizeFocusedOrigin(string? origin)
    {
        return PageZoomPreferenceSettingsProfile.TryNormalizeOrigin(origin, out var normalizedOrigin)
            ? normalizedOrigin
            : null;
    }

    private void UpdatePopupButtons()
    {
        var enabled = PopupRuleGrid.SelectedItem is PopupPermissionEntry;
        AllowPopupButton.IsEnabled = enabled;
        BlockPopupRuleButton.IsEnabled = enabled;
        ResetPopupRuleButton.IsEnabled = enabled;
    }

    private void UpdatePermissionButtons()
    {
        var enabled = PermissionGrid.SelectedItem is SitePermissionEntry && _profile is not null;
        AllowPermissionButton.IsEnabled = enabled;
        DenyPermissionButton.IsEnabled = enabled;
        ResetPermissionButton.IsEnabled = enabled;
    }

    private async void AllowPermissionButton_Click(object sender, RoutedEventArgs e)
    {
        await SetSelectedPermissionAsync(CoreWebView2PermissionState.Allow);
    }

    private async void DenyPermissionButton_Click(object sender, RoutedEventArgs e)
    {
        await SetSelectedPermissionAsync(CoreWebView2PermissionState.Deny);
    }

    private async void ResetPermissionButton_Click(object sender, RoutedEventArgs e)
    {
        await SetSelectedPermissionAsync(CoreWebView2PermissionState.Default);
    }

    private async Task SetSelectedPermissionAsync(CoreWebView2PermissionState state)
    {
        if (_profile is null || PermissionGrid.SelectedItem is not SitePermissionEntry entry)
        {
            return;
        }

        try
        {
            await SitePrivacyService.SetPermissionStateAsync(
                _profile,
                entry.Origin,
                entry.PermissionKind,
                state);
            await ReloadPermissionEntriesAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = "権限を変更できません: " + ex.Message;
        }
    }

    private async void ResetOriginButton_Click(object sender, RoutedEventArgs e)
    {
        if (_profile is null || PermissionGrid.SelectedItem is not SitePermissionEntry selected)
        {
            StatusText.Text = "既定へ戻すサイトを選択してください";
            return;
        }

        try
        {
            var entries = (PermissionGrid.ItemsSource as IEnumerable<SitePermissionEntry> ?? [])
                .Where(entry => entry.Origin.Equals(selected.Origin, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var entry in entries)
            {
                await SitePrivacyService.SetPermissionStateAsync(
                    _profile,
                    entry.Origin,
                    entry.PermissionKind,
                    CoreWebView2PermissionState.Default);
            }

            await ReloadPermissionEntriesAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = "サイト権限を既定へ戻せません: " + ex.Message;
        }
    }

    private void AllDataCheckBox_Click(object sender, RoutedEventArgs e)
    {
        var isChecked = AllDataCheckBox.IsChecked == true;
        CacheCheckBox.IsChecked = isChecked;
        SiteDataCheckBox.IsChecked = isChecked;
        BrowsingHistoryCheckBox.IsChecked = isChecked;
        DownloadHistoryCheckBox.IsChecked = isChecked;
    }

    private void PrepareClearDataButton_Click(object sender, RoutedEventArgs e)
    {
        if (ContainerBox.SelectedItem is not ContainerProfile container)
        {
            StatusText.Text = "コンテナを選択してください";
            return;
        }

        var categories = GetSelectedCategories();
        if (categories == SiteDataCategory.None)
        {
            StatusText.Text = "削除するデータを選択してください";
            return;
        }

        var (from, to) = GetSelectedPeriod();
        _pendingClearRequest = new SiteDataClearRequest(container.Id, categories, from, to);
        var periodName = (PeriodBox.SelectedItem as PeriodOption)?.Name ?? "全期間";
        ClearConfirmationText.Text =
            $"{container.Name}の{periodName}のデータを削除します。Cookieとサイトストレージを含む場合、サイトからログアウトする可能性があります。ダウンロード済みの実ファイルは削除しません。";
        ClearConfirmationPanel.Visibility = Visibility.Visible;
    }

    private async void ConfirmClearDataButton_Click(object sender, RoutedEventArgs e)
    {
        var request = _pendingClearRequest;
        CancelClearConfirmation();
        if (request is null || _profile is null)
        {
            StatusText.Text = "削除対象のプロファイルを読み込めません";
            return;
        }

        StatusText.Text = "閲覧データを削除しています...";
        try
        {
            await SitePrivacyService.ClearProfileDataAsync(
                _profile,
                request.Categories,
                request.From,
                request.To);
            var result = await _clearApplicationDataAsync(request);
            StatusText.Text =
                $"削除しました（アプリ履歴 {result.ApplicationBrowsingHistoryDeleted}件、ダウンロード履歴 {result.ApplicationDownloadHistoryDeleted}件）";
        }
        catch (Exception ex)
        {
            StatusText.Text = "閲覧データを削除できません: " + ex.Message;
        }
    }

    private void CancelClearDataButton_Click(object sender, RoutedEventArgs e)
    {
        CancelClearConfirmation();
    }

    private void CancelClearConfirmation()
    {
        _pendingClearRequest = null;
        ClearConfirmationPanel.Visibility = Visibility.Collapsed;
    }

    private SiteDataCategory GetSelectedCategories()
    {
        var categories = SiteDataCategory.None;
        if (CacheCheckBox.IsChecked == true)
        {
            categories |= SiteDataCategory.Cache;
        }

        if (SiteDataCheckBox.IsChecked == true)
        {
            categories |= SiteDataCategory.CookiesAndSiteData;
        }

        if (BrowsingHistoryCheckBox.IsChecked == true)
        {
            categories |= SiteDataCategory.BrowsingHistory;
        }

        if (DownloadHistoryCheckBox.IsChecked == true)
        {
            categories |= SiteDataCategory.DownloadHistory;
        }

        return categories;
    }

    private (DateTimeOffset? From, DateTimeOffset? To) GetSelectedPeriod()
    {
        var value = (PeriodBox.SelectedItem as PeriodOption)?.Value ?? "all";
        if (value == "all")
        {
            return (null, null);
        }

        var today = DateTime.Today;
        var toDate = today.AddDays(1);
        var fromDate = value switch
        {
            "today" => today,
            "7days" => today.AddDays(-6),
            "30days" => today.AddDays(-29),
            _ => today
        };
        return (
            new DateTimeOffset(fromDate, TimeZoneInfo.Local.GetUtcOffset(fromDate)),
            new DateTimeOffset(toDate, TimeZoneInfo.Local.GetUtcOffset(toDate)));
    }

    private void DisposeProfileWebView()
    {
        if (_profileWebView is null)
        {
            return;
        }

        ProfileWebViewHost.Children.Remove(_profileWebView);
        _profileWebView.Dispose();
        _profileWebView = null;
        _profile = null;
    }

    private sealed record PeriodOption(string Name, string Value);
}
