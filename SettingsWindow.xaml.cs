using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using TabBrowser.Models;
using TabBrowser.Services;

namespace TabBrowser;

public partial class SettingsWindow : Window
{

    private const int GeneralSettingsTabIndex = 0;
    private const int AppearanceSettingsTabIndex = 1;
    private const int MediaSettingsTabIndex = 2;
    private const int SiteSettingsTabIndex = 3;
    private const int IntegrationSettingsTabIndex = 4;
    private const double MinimumCpuSaverDelaySeconds = 15;
    private const double MaximumCpuSaverDelaySeconds = 3600;

    private readonly ContainerProfileStore _containerProfileStore = new();
    private readonly HashSet<string> _protectedContainerProfileIds;
    private readonly AppSettings _workingSettings;
    private readonly ObservableCollection<SearchAliasSettings> _searchAliases;

    private List<ContainerProfile> _containerProfiles = [];
    private IReadOnlyList<GestureCommandOption> _gestureCommandOptions =
        GestureSettingsCatalog.CommandOptions;
    private MouseGestureSettingsWindow? _mouseGestureSettingsWindow;

    private bool _suppressRightClickUntilRelease;
    private bool _isUpdatingYouTubeMiniPlayerQualitySliders;

    public SettingsWindow(AppSettings settings)
        : this(settings, [])
    {
    }

    public SettingsWindow(AppSettings settings, IEnumerable<string>? protectedContainerProfileIds)
    {
        InitializeComponent();
        SettingsTabControl.SelectionChanged += SettingsTabControl_SelectionChanged;
        ApplySettingsTabSelection(SettingsTabControl.SelectedIndex);

        _protectedContainerProfileIds = (protectedContainerProfileIds ?? [])
            .Select(ContainerProfile.NormalizeId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _workingSettings = CloneSettings(settings);
        SearchEngineBox.Text = _workingSettings.SearchEngine;
        SearchUrlTemplateBox.Text = _workingSettings.SearchUrlTemplate;
        _searchAliases = new ObservableCollection<SearchAliasSettings>(
            SearchAliasSettingsProfile.Normalize(_workingSettings.SearchAliases));
        SearchAliasList.ItemsSource = _searchAliases;
        EnableQuickLookCheckBox.IsChecked = _workingSettings.QuickLook.IsEnabled;
        QuickLookRightPaneCheckBox.IsChecked = _workingSettings.QuickLook.UseRightSidePane;
        QuickLookPanePlacementBox.SelectedIndex = string.Equals(
            _workingSettings.QuickLook.PanePlacement,
            QuickLookPanePlacement.Bottom,
            StringComparison.Ordinal)
                ? 1
                : 0;
        QuickLookTextFontSizeBox.Text = _workingSettings.QuickLook.TextFontSize.ToString(CultureInfo.CurrentCulture);
        QuickLookTextWrapCheckBox.IsChecked = _workingSettings.QuickLook.TextWrap;
        QuickLookAutoPlayMediaCheckBox.IsChecked = _workingSettings.QuickLook.AutoPlayMedia;
        EnableYouTubeMiniPlayerHoverAudioPreviewCheckBox.IsChecked =
            _workingSettings.EnableYouTubeMiniPlayerHoverAudioPreview;
        EnableYouTubeMiniPlayerSizeBasedQualityCheckBox.IsChecked =
            _workingSettings.EnableYouTubeMiniPlayerSizeBasedQuality;
        ApplyYouTubeMiniPlayerQualityThresholdsToSliders(
            _workingSettings.YouTubeMiniPlayerQualityLowWidthThreshold,
            _workingSettings.YouTubeMiniPlayerQualityHighWidthThreshold);
        EnableCpuSaverCheckBox.IsChecked = _workingSettings.EnableFocusedViewingCpuSaver;
        CpuSaverDelayBox.Text = _workingSettings.FocusedViewingCpuSaverDelaySeconds.ToString(CultureInfo.CurrentCulture);
        EnableInactiveTabMemorySaverCheckBox.IsChecked = _workingSettings.EnableInactiveTabMemorySaver;
        EnableLinkHoverPreviewCheckBox.IsChecked = _workingSettings.EnableLinkHoverPreview;
        EnableClipboardHistoryCheckBox.IsChecked = _workingSettings.ClipboardHistory.IsEnabled;
        ClipboardHistoryMaximumItemCountBox.Text = _workingSettings.ClipboardHistory.MaximumItemCount.ToString(
            CultureInfo.CurrentCulture);
        CaptureClipboardUrlsCheckBox.IsChecked = _workingSettings.ClipboardHistory.CaptureUrls;
        CaptureClipboardFilesCheckBox.IsChecked = _workingSettings.ClipboardHistory.CaptureFiles;
        _workingSettings.BrowsingHistory = BrowsingHistorySettingsProfile.Normalize(
            _workingSettings.BrowsingHistory);
        EnableBrowsingHistoryCheckBox.IsChecked = _workingSettings.BrowsingHistory.IsEnabled;
        BrowsingHistoryExcludedHostsBox.Text = string.Join(
            Environment.NewLine,
            _workingSettings.BrowsingHistory.ExcludedHosts);
        EnableExtensionsCheckBox.IsChecked = _workingSettings.EnableBrowserExtensions;
        EnableChatGptReadabilityCheckBox.IsChecked = _workingSettings.ChatGptReadability.IsEnabled;
        ApplyFiveChLiveReadabilityToInputs(_workingSettings.FiveChLiveReadability);

        ApplyDownloadSettingsToInputs(_workingSettings.Downloads);
        ApplyToolbarVisibilityToInputs(_workingSettings.ToolbarVisibility);

        ReloadContainerProfileInputs();
        UpdateGestureSummary();
    }

    public AppSettings ResultSettings { get; private set; } = new();

    public bool IsAccepted { get; private set; }

    public bool ContainerProfilesChanged { get; private set; }

    private void ApplyYouTubeMiniPlayerQualityThresholdsToSliders(
        int lowWidthThreshold,
        int highWidthThreshold)
    {
        var normalized = YouTubeMiniPlayerQualitySettingsProfile.Normalize(
            lowWidthThreshold,
            highWidthThreshold);
        _isUpdatingYouTubeMiniPlayerQualitySliders = true;
        try
        {
            YouTubeMiniPlayerQualityLowWidthSlider.Value = normalized.LowWidthThreshold;
            YouTubeMiniPlayerQualityHighWidthSlider.Value = normalized.HighWidthThreshold;
        }
        finally
        {
            _isUpdatingYouTubeMiniPlayerQualitySliders = false;
        }

        UpdateYouTubeMiniPlayerQualityThresholdSummary(
            normalized.LowWidthThreshold,
            normalized.HighWidthThreshold);
    }

    private void YouTubeMiniPlayerQualityWidthSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingYouTubeMiniPlayerQualitySliders
            || YouTubeMiniPlayerQualityLowWidthSlider is null
            || YouTubeMiniPlayerQualityHighWidthSlider is null)
        {
            return;
        }

        var low = (int)Math.Round(YouTubeMiniPlayerQualityLowWidthSlider.Value);
        var high = (int)Math.Round(YouTubeMiniPlayerQualityHighWidthSlider.Value);
        if (ReferenceEquals(sender, YouTubeMiniPlayerQualityLowWidthSlider)
            && high - low < YouTubeMiniPlayerQualitySettingsProfile.MinimumThresholdGap)
        {
            high = Math.Min(
                YouTubeMiniPlayerQualitySettingsProfile.MaximumWidthThreshold,
                low + YouTubeMiniPlayerQualitySettingsProfile.MinimumThresholdGap);
        }
        else if (ReferenceEquals(sender, YouTubeMiniPlayerQualityHighWidthSlider)
                 && high - low < YouTubeMiniPlayerQualitySettingsProfile.MinimumThresholdGap)
        {
            low = Math.Max(
                YouTubeMiniPlayerQualitySettingsProfile.MinimumWidthThreshold,
                high - YouTubeMiniPlayerQualitySettingsProfile.MinimumThresholdGap);
        }

        ApplyYouTubeMiniPlayerQualityThresholdsToSliders(low, high);
    }

    private void UpdateYouTubeMiniPlayerQualityThresholdSummary(
        int lowWidthThreshold,
        int highWidthThreshold)
    {
        if (YouTubeMiniPlayerQualityLowWidthValueText is null
            || YouTubeMiniPlayerQualityHighWidthValueText is null
            || YouTubeMiniPlayerQualityRangeSummaryText is null)
        {
            return;
        }

        YouTubeMiniPlayerQualityLowWidthValueText.Text = $"{lowWidthThreshold}px";
        YouTubeMiniPlayerQualityHighWidthValueText.Text = $"{highWidthThreshold}px";
        YouTubeMiniPlayerQualityRangeSummaryText.Text =
            $"幅{lowWidthThreshold}px未満: 最大720p\n"
            + $"幅{lowWidthThreshold}px以上～{highWidthThreshold}px未満: 最大1080p\n"
            + $"幅{highWidthThreshold}px以上: 最大1440p\n"
            + "全画面: 最高画質";
    }

    public void SetGestureCommandOptions(IReadOnlyList<GestureCommandOption>? options)
    {
        if (options is { Count: > 0 })
        {
            _gestureCommandOptions = options;
        }
    }

    public void UpdateQuickLookLayoutSettings(QuickLookSettings settings)
    {
        var normalized = QuickLookSettingsProfile.Normalize(settings);
        var working = QuickLookSettingsProfile.Normalize(_workingSettings.QuickLook);
        working.UseRightSidePane = normalized.UseRightSidePane;
        working.PanePlacement = normalized.PanePlacement;
        working.RightPaneWidth = normalized.RightPaneWidth;
        working.BottomPaneHeight = normalized.BottomPaneHeight;
        working.FloatingOffsetX = normalized.FloatingOffsetX;
        working.FloatingOffsetY = normalized.FloatingOffsetY;
        working.FloatingWidth = normalized.FloatingWidth;
        working.FloatingHeight = normalized.FloatingHeight;
        _workingSettings.QuickLook = working;
        QuickLookRightPaneCheckBox.IsChecked = normalized.UseRightSidePane;
        QuickLookPanePlacementBox.SelectedIndex = string.Equals(
            normalized.PanePlacement,
            QuickLookPanePlacement.Bottom,
            StringComparison.Ordinal)
                ? 1
                : 0;
    }

    public void UpdateQuickLookTextDisplaySettings(QuickLookSettings settings)
    {
        var normalized = QuickLookSettingsProfile.Normalize(settings);
        var working = QuickLookSettingsProfile.Normalize(_workingSettings.QuickLook);
        working.TextFontSize = normalized.TextFontSize;
        working.TextWrap = normalized.TextWrap;
        _workingSettings.QuickLook = working;
        QuickLookTextFontSizeBox.Text = normalized.TextFontSize.ToString(CultureInfo.CurrentCulture);
        QuickLookTextWrapCheckBox.IsChecked = normalized.TextWrap;
    }

    private async void OpenGestureSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_mouseGestureSettingsWindow is { IsLoaded: true } existing)
        {
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            existing.Activate();
            return;
        }

        var dialog = new MouseGestureSettingsWindow(
            _workingSettings,
            commandOptions: _gestureCommandOptions)
        {
            Owner = this
        };
        _mouseGestureSettingsWindow = dialog;

        try
        {
            await ModelessWindowService.ShowAsync(dialog);
        }
        finally
        {
            if (ReferenceEquals(_mouseGestureSettingsWindow, dialog))
            {
                _mouseGestureSettingsWindow = null;
            }
        }

        if (!dialog.IsAccepted || !IsLoaded)
        {
            return;
        }

        _workingSettings.GestureStartThreshold = dialog.ResultSettings.GestureStartThreshold;
        _workingSettings.GestureDirectionThreshold = dialog.ResultSettings.GestureDirectionThreshold;
        _workingSettings.PreserveContextMenuOnShortRightClick = dialog.ResultSettings.PreserveContextMenuOnShortRightClick;
        _workingSettings.GestureMappings = GestureSettingsCatalog.NormalizeMappings(dialog.ResultSettings.GestureMappings);
        UpdateGestureSummary();
        SetError(string.Empty);
    }

    private void BrowseDownloadDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "既定のダウンロード保存先を選択",
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(DefaultDownloadDirectoryBox.Text))
        {
            dialog.InitialDirectory = DefaultDownloadDirectoryBox.Text.Trim().Trim('"');
        }

        if (dialog.ShowDialog(this) == true)
        {
            DefaultDownloadDirectoryBox.Text = dialog.FolderName;
        }
    }

    private void ContainerProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplySelectedContainerProfileToInputs();
    }

    private void AddContainerProfileButton_Click(object sender, RoutedEventArgs e)
    {
        var now = DateTimeOffset.UtcNow;
        var profile = new ContainerProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = CreateContainerProfileName(),
            Color = "#22C55E",
            IsDefault = false,
            CreatedAt = now,
            LastUsedAt = now
        };

        _containerProfiles.Add(profile);
        SaveContainerProfiles(profile.Id);
        SetError(string.Empty);
    }

    private void SaveContainerProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedContainerProfile() is not { } profile || profile.IsDefault)
        {
            return;
        }

        if (!TryReadContainerProfileInputs(out var name, out var color))
        {
            return;
        }

        profile.Name = name;
        profile.Color = color;
        profile.LastUsedAt = DateTimeOffset.UtcNow;
        SaveContainerProfiles(profile.Id);
        SetError(string.Empty);
    }

    private void DeleteContainerProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedContainerProfile() is not { } profile || profile.IsDefault)
        {
            return;
        }

        if (IsProtectedContainerProfile(profile.Id))
        {
            SetError("使用中のコンテナは削除できません。開いているタブと閉じたタブ履歴から外してから削除してください。");
            return;
        }

        var result = MessageBox.Show(
            this,
            $"コンテナ「{profile.Name}」を一覧から削除します。WebView2 データは削除しません。",
            "コンテナ削除",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _containerProfiles.Remove(profile);
        SaveContainerProfiles(ContainerProfile.DefaultId);
        SetError(string.Empty);
    }

    private void DeleteContainerProfileDataButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedContainerProfile() is not { } profile || profile.IsDefault)
        {
            return;
        }

        if (IsProtectedContainerProfile(profile.Id))
        {
            SetError("使用中のコンテナデータは削除できません。対象コンテナのタブを閉じてから実行してください。");
            return;
        }

        if (!TryGetSafeContainerProfileDataFolder(profile, out var profileDataFolder))
        {
            SetError("コンテナデータの保存先を安全に確認できません。");
            return;
        }

        var result = MessageBox.Show(
            this,
            $"コンテナ「{profile.Name}」の WebView2 データを削除します。ログイン状態やキャッシュも削除されます。コンテナ一覧からは削除しません。",
            "コンテナデータ削除",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            if (Directory.Exists(profileDataFolder))
            {
                Directory.Delete(profileDataFolder, recursive: true);
            }

            SetError(string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetError("コンテナデータを削除できませんでした。対象コンテナのタブが残っていないか確認してください: " + ex.Message);
        }
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildResult(out var result))
        {
            return;
        }

        ResultSettings = result;
        IsAccepted = true;
        Close();
    }

    private void AddSearchAliasButton_Click(object sender, RoutedEventArgs e)
    {
        var alias = new SearchAliasSettings
        {
            SearchUrlTemplate = SearchUrlTemplateBox.Text.Trim()
        };
        _searchAliases.Add(alias);
        SearchAliasList.SelectedItem = alias;
        SearchAliasList.ScrollIntoView(alias);
    }

    private void DeleteSearchAliasButton_Click(object sender, RoutedEventArgs e)
    {
        if (SearchAliasList.SelectedItem is SearchAliasSettings alias)
        {
            _searchAliases.Remove(alias);
        }
    }

    private void SearchAliasEditor_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SearchAliasSettings alias })
        {
            SearchAliasList.SelectedItem = alias;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {

        IsAccepted = false;
        Close();
    }

    private void SettingsTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplySettingsTabSelection(SettingsTabControl.SelectedIndex);
        SettingsScrollViewer.ScrollToTop();
    }

    private void SettingsTabControl_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0)
        {
            return;
        }

        e.Handled = MoveSettingsTabSelection(e.Delta);
    }

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled
            || e.Delta == 0
            || Mouse.RightButton != MouseButtonState.Pressed
            || !MoveSettingsTabSelection(e.Delta))
        {
            return;
        }

        _suppressRightClickUntilRelease = true;
        e.Handled = true;
    }

    private void Window_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_suppressRightClickUntilRelease)
        {
            return;
        }

        _suppressRightClickUntilRelease = false;
        e.Handled = true;
    }

    private bool MoveSettingsTabSelection(int wheelDelta)
    {
        var tabCount = SettingsTabControl.Items.Count;
        if (tabCount < 2 || wheelDelta == 0)
        {
            return false;
        }

        var currentIndex = Math.Clamp(SettingsTabControl.SelectedIndex, 0, tabCount - 1);
        var offset = wheelDelta < 0 ? 1 : -1;
        SettingsTabControl.SelectedIndex = (currentIndex + offset + tabCount) % tabCount;
        return true;
    }

    private void ApplySettingsTabSelection(int selectedIndex)
    {
        SetSectionVisibility(selectedIndex == GeneralSettingsTabIndex,
            SearchSettingsSection,
            BehaviorSettingsSection,
            BrowserSettingsSection);
        SetSectionVisibility(selectedIndex == AppearanceSettingsTabIndex,
            QuickLookSettingsSection,
            ToolbarSettingsSection,
            GestureSettingsSection);
        SetSectionVisibility(selectedIndex == MediaSettingsTabIndex,
            YouTubeMiniPlayerSettingsSection,

            DownloadSettingsSection);

        SetSectionVisibility(selectedIndex == SiteSettingsTabIndex,
            FiveChSettingsSection,
            ChatGptSettingsSection);
        SetSectionVisibility(selectedIndex == IntegrationSettingsTabIndex,
            ClipboardHistorySettingsSection,
            BrowsingHistorySettingsSection,

            ContainerSettingsSection);

    }

    private static void SetSectionVisibility(bool isVisible, params FrameworkElement[] sections)
    {
        var visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
        foreach (var section in sections)
        {
            section.Visibility = visibility;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        SettingsTabControl.SelectionChanged -= SettingsTabControl_SelectionChanged;

        base.OnClosed(e);
    }

    protected override void OnDeactivated(EventArgs e)
    {
        _suppressRightClickUntilRelease = false;
        base.OnDeactivated(e);
    }

    private bool TryBuildResult(out AppSettings result)
    {
        result = CloneSettings(_workingSettings);

        var searchEngine = SearchEngineBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(searchEngine))
        {
            SetValidationError(SearchEngineBox, "検索エンジン名を入力してください。");
            return false;
        }

        var searchUrlTemplate = SearchUrlTemplateBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(searchUrlTemplate) || !searchUrlTemplate.Contains("{0}", StringComparison.Ordinal))
        {
            SetValidationError(SearchUrlTemplateBox, "検索 URL には {0} を含めてください。");
            return false;
        }

        try
        {
            _ = string.Format(CultureInfo.InvariantCulture, searchUrlTemplate, "test");
        }
        catch (FormatException)
        {
            SetValidationError(SearchUrlTemplateBox, "検索 URL の形式が正しくありません。");
            return false;
        }

        if (!TryReadDouble(
                CpuSaverDelayBox,
                "待機秒数",
                MinimumCpuSaverDelaySeconds,
                MaximumCpuSaverDelaySeconds,
                out var cpuSaverDelaySeconds))
        {
            return false;
        }

        if (!TryReadInt(
                ClipboardHistoryMaximumItemCountBox,
                "クリップボード履歴件数",
                ClipboardHistorySettingsProfile.MinimumMaximumItemCount,
                ClipboardHistorySettingsProfile.MaximumMaximumItemCount,
                out var clipboardHistoryMaximumItemCount))
        {
            return false;
        }

        if (!TryReadInt(
                QuickLookTextFontSizeBox,
                "QuickLookテキスト文字サイズ",
                QuickLookSettingsProfile.MinimumTextFontSize,
                QuickLookSettingsProfile.MaximumTextFontSize,
                out var quickLookTextFontSize))
        {
            return false;
        }

        if (!TryReadInt(
                DownloadHistoryRetentionLimitBox,
                "履歴保持件数",
                0,
                DownloadSettingsProfile.MaximumHistoryRetentionLimit,
                out var historyRetentionLimit))
        {
            return false;
        }

        if (!TryReadInt(
                FiveChFontSizeBox,
                "5ch本文文字サイズ",
                FiveChLiveReadabilityProfile.MinimumFontSize,
                FiveChLiveReadabilityProfile.MaximumFontSize,
                out var fiveChFontSize)
            || !TryReadInt(
                FiveChPostSpacingBox,
                "5chレス間隔",
                FiveChLiveReadabilityProfile.MinimumPostSpacing,
                FiveChLiveReadabilityProfile.MaximumPostSpacing,
                out var fiveChPostSpacing))
        {
            return false;
        }

        if (!TryReadInt(
                FiveChRefreshIntervalBox,
                "5ch自動更新間隔",
                FiveChLiveReadabilityProfile.MinimumRefreshIntervalSeconds,
                FiveChLiveReadabilityProfile.MaximumRefreshIntervalSeconds,
                out var fiveChRefreshIntervalSeconds))
        {
            return false;
        }

        result.SearchEngine = searchEngine;
        result.SearchUrlTemplate = searchUrlTemplate;
        if (!TryBuildSearchAliases(out var searchAliases))
        {
            return false;
        }

        result.SearchAliases = searchAliases;
        result.QuickLook.IsEnabled = EnableQuickLookCheckBox.IsChecked == true;
        result.QuickLook.UseRightSidePane = QuickLookRightPaneCheckBox.IsChecked == true;
        result.QuickLook.PanePlacement = QuickLookPanePlacementBox.SelectedIndex == 1
            ? QuickLookPanePlacement.Bottom
            : QuickLookPanePlacement.Right;
        result.QuickLook.TextFontSize = quickLookTextFontSize;
        result.QuickLook.TextWrap = QuickLookTextWrapCheckBox.IsChecked == true;
        result.QuickLook.AutoPlayMedia = QuickLookAutoPlayMediaCheckBox.IsChecked == true;
        result.QuickLook = QuickLookSettingsProfile.Normalize(result.QuickLook);
        result.EnableYouTubeMiniPlayerHoverAudioPreview =
            EnableYouTubeMiniPlayerHoverAudioPreviewCheckBox.IsChecked == true;
        result.EnableYouTubeMiniPlayerSizeBasedQuality =
            EnableYouTubeMiniPlayerSizeBasedQualityCheckBox.IsChecked == true;
        var miniPlayerQualityThresholds = YouTubeMiniPlayerQualitySettingsProfile.Normalize(
            (int)Math.Round(YouTubeMiniPlayerQualityLowWidthSlider.Value),
            (int)Math.Round(YouTubeMiniPlayerQualityHighWidthSlider.Value));
        result.YouTubeMiniPlayerQualityLowWidthThreshold =
            miniPlayerQualityThresholds.LowWidthThreshold;
        result.YouTubeMiniPlayerQualityHighWidthThreshold =
            miniPlayerQualityThresholds.HighWidthThreshold;
        result.EnableFocusedViewingCpuSaver = EnableCpuSaverCheckBox.IsChecked == true;
        result.FocusedViewingCpuSaverDelaySeconds = cpuSaverDelaySeconds;
        result.EnableInactiveTabMemorySaver = EnableInactiveTabMemorySaverCheckBox.IsChecked == true;
        result.EnableLinkHoverPreview = EnableLinkHoverPreviewCheckBox.IsChecked == true;
        result.ClipboardHistory = ClipboardHistorySettingsProfile.Normalize(new ClipboardHistorySettings
        {
            IsEnabled = EnableClipboardHistoryCheckBox.IsChecked == true,
            MaximumItemCount = clipboardHistoryMaximumItemCount,
            CaptureUrls = CaptureClipboardUrlsCheckBox.IsChecked == true,
            CaptureFiles = CaptureClipboardFilesCheckBox.IsChecked == true
        });
        if (!TryReadBrowsingHistoryHosts(out var browsingHistoryExcludedHosts))
        {
            return false;
        }

        result.BrowsingHistory = BrowsingHistorySettingsProfile.Normalize(new BrowsingHistorySettings
        {
            IsEnabled = EnableBrowsingHistoryCheckBox.IsChecked == true,
            ExcludedHosts = browsingHistoryExcludedHosts,
            ExcludedContainerProfileIds = BrowsingHistoryExcludedContainersPanel.Children
                .OfType<CheckBox>()
                .Where(static checkBox => checkBox.IsChecked == true)
                .Select(static checkBox => checkBox.Tag as string ?? string.Empty)
                .ToList()
        });
        result.EnableBrowserExtensions = EnableExtensionsCheckBox.IsChecked == true;
        result.ChatGptReadability.IsEnabled = EnableChatGptReadabilityCheckBox.IsChecked == true;
        result.FiveChLiveReadability = FiveChLiveReadabilityProfile.Normalize(new FiveChLiveReadabilitySettings
        {
            IsEnabled = EnableFiveChLiveReadabilityCheckBox.IsChecked == true,
            HighlightNewPosts = HighlightFiveChNewPostsCheckBox.IsChecked == true,
            AutoRefresh = EnableFiveChAutoRefreshCheckBox.IsChecked == true,
            RefreshIntervalSeconds = fiveChRefreshIntervalSeconds,
            AutoOpenNextThread = EnableFiveChAutoOpenNextThreadCheckBox.IsChecked == true,
            AutoScroll = EnableFiveChAutoScrollCheckBox.IsChecked == true,
            PreviewReplyAnchors = PreviewFiveChReplyAnchorsCheckBox.IsChecked == true,
            HighlightPosterIds = HighlightFiveChPosterIdsCheckBox.IsChecked == true,
            FontSize = fiveChFontSize,
            PostSpacing = fiveChPostSpacing
        });

        result.Downloads = DownloadSettingsProfile.Normalize(new DownloadSettings
        {
            DefaultSaveDirectory = DefaultDownloadDirectoryBox.Text,
            AskSaveLocation = AskDownloadLocationCheckBox.IsChecked == true,
            OpenShelfOnDownloadStart = OpenDownloadShelfOnStartCheckBox.IsChecked == true,
            OpenShelfOnDownloadCompleted = OpenDownloadShelfOnCompleteCheckBox.IsChecked == true,
            HistoryRetentionLimit = historyRetentionLimit
        });
        if (!TryApplyToolbarVisibilityFromInputs(result.ToolbarVisibility))
        {
            return false;
        }

        result.GestureMappings = GestureSettingsCatalog.NormalizeMappings(_workingSettings.GestureMappings);
        SetError(string.Empty);
        return true;
    }

    private bool TryBuildSearchAliases(out List<SearchAliasSettings> aliases)
    {
        aliases = [];
        var knownAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in _searchAliases)
        {
            var name = alias.Alias.Trim();
            if (!SearchAliasSettingsProfile.IsValidAlias(name))
            {
                SearchAliasList.SelectedItem = alias;
                SetValidationError(
                    SearchAliasList,
                    $"検索エイリアスは1～{SearchAliasSettingsProfile.MaximumAliasLength}文字の半角英数字、_、-で入力してください。");
                return false;
            }

            if (!knownAliases.Add(name))
            {
                SearchAliasList.SelectedItem = alias;
                SetValidationError(SearchAliasList, $"検索エイリアスが重複しています: {name}");
                return false;
            }

            if (!SearchAliasSettingsProfile.IsValidSearchUrlTemplate(alias.SearchUrlTemplate))
            {
                SearchAliasList.SelectedItem = alias;
                SetValidationError(
                    SearchAliasList,
                    $"検索エイリアス「{name}」のURLは、{{0}}を含むhttp／https URLにしてください。");
                return false;
            }
        }

        aliases = SearchAliasSettingsProfile.Normalize(_searchAliases);
        return true;
    }

    private void ReloadContainerProfileInputs(string? selectProfileId = null)
    {
        _containerProfiles = _containerProfileStore.Load();
        ContainerProfileList.ItemsSource = null;
        ContainerProfileList.ItemsSource = _containerProfiles;

        var normalizedSelectProfileId = ContainerProfile.NormalizeId(selectProfileId);
        ContainerProfileList.SelectedItem = _containerProfiles.FirstOrDefault(profile => string.Equals(
                profile.Id,
                normalizedSelectProfileId,
                StringComparison.OrdinalIgnoreCase))
            ?? _containerProfiles.FirstOrDefault(static profile => profile.IsDefault);

        ApplySelectedContainerProfileToInputs();
        ReloadBrowsingHistoryContainerInputs();
    }

    private void ReloadBrowsingHistoryContainerInputs()
    {
        var excludedIds = BrowsingHistorySettingsProfile.Normalize(_workingSettings.BrowsingHistory)
            .ExcludedContainerProfileIds
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        BrowsingHistoryExcludedContainersPanel.Children.Clear();
        foreach (var profile in _containerProfiles)
        {
            BrowsingHistoryExcludedContainersPanel.Children.Add(new CheckBox
            {
                Content = profile.Name,
                Tag = profile.Id,
                IsChecked = excludedIds.Contains(profile.Id),
                Margin = new Thickness(0, 0, 12, 4)
            });
        }
    }

    private bool TryReadBrowsingHistoryHosts(out List<string> hosts)
    {
        var values = BrowsingHistoryExcludedHostsBox.Text
            .Split([',', ';', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        hosts = [];
        foreach (var value in values)
        {
            var normalized = BrowsingHistorySettingsProfile.NormalizeHost(value);
            if (normalized.Length == 0)
            {
                SetValidationError(
                    BrowsingHistoryExcludedHostsBox,
                    $"履歴除外サイトの形式が正しくありません: {value}");
                return false;
            }

            hosts.Add(normalized);
        }

        return true;
    }

    private void ApplySelectedContainerProfileToInputs()
    {
        var profile = GetSelectedContainerProfile();
        var canEdit = profile is { IsDefault: false };
        var isProtected = profile is not null && IsProtectedContainerProfile(profile.Id);

        ContainerProfileNameBox.Text = profile?.Name ?? string.Empty;
        ContainerProfileColorBox.Text = profile?.Color ?? ContainerProfile.DefaultColor;
        ContainerProfileNameBox.IsEnabled = canEdit;
        ContainerProfileColorBox.IsEnabled = canEdit;
        SaveContainerProfileButton.IsEnabled = canEdit;
        DeleteContainerProfileButton.IsEnabled = canEdit && !isProtected;
        DeleteContainerProfileDataButton.IsEnabled = canEdit && !isProtected;
    }

    private ContainerProfile? GetSelectedContainerProfile()
    {
        return ContainerProfileList.SelectedItem as ContainerProfile;
    }

    private void SaveContainerProfiles(string? selectProfileId)
    {
        _containerProfileStore.Save(_containerProfiles);
        ContainerProfilesChanged = true;
        ReloadContainerProfileInputs(selectProfileId);
    }

    private string CreateContainerProfileName()
    {
        var existingNames = _containerProfiles
            .Select(static profile => profile.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var index = 1; index < 1000; index++)
        {
            var candidate = $"コンテナ {index}";
            if (!existingNames.Contains(candidate))
            {
                return candidate;
            }
        }

        return "コンテナ";
    }

    private bool TryReadContainerProfileInputs(out string name, out string color)
    {
        name = ContainerProfileNameBox.Text.Trim();
        color = ContainerProfileColorBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            SetError("コンテナ名を入力してください。");
            return false;
        }

        if (name.Length > 40)
        {
            SetError("コンテナ名は40文字以内にしてください。");
            return false;
        }

        if (!IsValidContainerProfileColor(color))
        {
            SetError("コンテナ色は #22C55E のような16進カラーで指定してください。");
            return false;
        }

        return true;
    }

    private bool IsProtectedContainerProfile(string? containerProfileId)
    {
        var normalizedId = ContainerProfile.NormalizeId(containerProfileId);
        return string.Equals(normalizedId, ContainerProfile.DefaultId, StringComparison.OrdinalIgnoreCase)
            || _protectedContainerProfileIds.Contains(normalizedId);
    }

    private static bool TryGetSafeContainerProfileDataFolder(ContainerProfile profile, out string profileDataFolder)
    {
        profileDataFolder = string.Empty;
        var normalizedId = ContainerProfile.NormalizeId(profile.Id);
        if (profile.IsDefault
            || string.Equals(normalizedId, ContainerProfile.DefaultId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(normalizedId, profile.Id, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var root = Path.GetFullPath(AppPaths.ContainerProfilesFolder);
        var folder = Path.GetFullPath(AppPaths.GetContainerProfileWebView2Folder(normalizedId));
        var rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        if (!folder.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        profileDataFolder = folder;
        return true;
    }

    private static bool IsValidContainerProfileColor(string color)
    {
        if (color.Length is not (7 or 9) || color[0] != '#')
        {
            return false;
        }

        return color
            .Skip(1)
            .All(static character => Uri.IsHexDigit(character));
    }

    private void ApplyDownloadSettingsToInputs(DownloadSettings settings)
    {
        settings = DownloadSettingsProfile.Normalize(settings);
        DefaultDownloadDirectoryBox.Text = settings.DefaultSaveDirectory;
        AskDownloadLocationCheckBox.IsChecked = settings.AskSaveLocation;
        OpenDownloadShelfOnStartCheckBox.IsChecked = settings.OpenShelfOnDownloadStart;
        OpenDownloadShelfOnCompleteCheckBox.IsChecked = settings.OpenShelfOnDownloadCompleted;
        DownloadHistoryRetentionLimitBox.Text = settings.HistoryRetentionLimit.ToString(CultureInfo.CurrentCulture);
    }

    private void ApplyToolbarVisibilityToInputs(ToolbarVisibilitySettings toolbarVisibility)
    {
        AutoHideToolbarCheckBox.IsChecked = toolbarVisibility.AutoHideToolbar;
        ShowBackButtonCheckBox.IsChecked = toolbarVisibility.ShowBackButton;
        ShowForwardButtonCheckBox.IsChecked = toolbarVisibility.ShowForwardButton;
        ShowReloadButtonCheckBox.IsChecked = toolbarVisibility.ShowReloadButton;
        ShowRestoreButtonCheckBox.IsChecked = toolbarVisibility.ShowRestoreButton;
        ShowClosedTabHistoryButtonCheckBox.IsChecked = toolbarVisibility.ShowClosedTabHistoryButton;
        ShowAddressBoxCheckBox.IsChecked = toolbarVisibility.ShowAddressBox;
        ShowGoButtonCheckBox.IsChecked = toolbarVisibility.ShowGoButton;
        ShowActionsButtonCheckBox.IsChecked = toolbarVisibility.ShowActionsButton;
        ShowAddBookmarkButtonCheckBox.IsChecked = toolbarVisibility.ShowAddBookmarkButton;
        ShowBookmarkListButtonCheckBox.IsChecked = toolbarVisibility.ShowBookmarkListButton;
        ShowWorkspaceOSButtonCheckBox.IsChecked = toolbarVisibility.ShowWorkspaceOSButton;

        ShowToolsButtonCheckBox.IsChecked = toolbarVisibility.ShowToolsButton;
        ShowAddExtensionButtonCheckBox.IsChecked = toolbarVisibility.ShowAddExtensionButton;
        ShowImportExtensionButtonCheckBox.IsChecked = toolbarVisibility.ShowImportExtensionButton;
        ShowManageExtensionsButtonCheckBox.IsChecked = toolbarVisibility.ShowManageExtensionsButton;
        ShowGestureSettingsButtonCheckBox.IsChecked = toolbarVisibility.ShowGestureSettingsButton;
        ShowSettingsButtonCheckBox.IsChecked = toolbarVisibility.ShowSettingsButton;
        ShowExtensionIconsCheckBox.IsChecked = toolbarVisibility.ShowExtensionIcons;
        ShowStatusTextCheckBox.IsChecked = toolbarVisibility.ShowStatusText;
    }

    private bool TryApplyToolbarVisibilityFromInputs(ToolbarVisibilitySettings toolbarVisibility)
    {
        toolbarVisibility.AutoHideToolbar = AutoHideToolbarCheckBox.IsChecked == true;
        toolbarVisibility.ShowBackButton = ShowBackButtonCheckBox.IsChecked == true;
        toolbarVisibility.ShowForwardButton = ShowForwardButtonCheckBox.IsChecked == true;
        toolbarVisibility.ShowReloadButton = ShowReloadButtonCheckBox.IsChecked == true;
        toolbarVisibility.ShowRestoreButton = ShowRestoreButtonCheckBox.IsChecked == true;
        toolbarVisibility.ShowClosedTabHistoryButton = ShowClosedTabHistoryButtonCheckBox.IsChecked == true;
        toolbarVisibility.ShowAddressBox = ShowAddressBoxCheckBox.IsChecked == true;
        toolbarVisibility.ShowGoButton = ShowGoButtonCheckBox.IsChecked == true;
        toolbarVisibility.ShowActionsButton = ShowActionsButtonCheckBox.IsChecked == true;
        toolbarVisibility.ShowAddBookmarkButton = ShowAddBookmarkButtonCheckBox.IsChecked == true;
        toolbarVisibility.ShowBookmarkListButton = ShowBookmarkListButtonCheckBox.IsChecked == true;
        toolbarVisibility.ShowWorkspaceOSButton = ShowWorkspaceOSButtonCheckBox.IsChecked == true;

        toolbarVisibility.ShowToolsButton = ShowToolsButtonCheckBox.IsChecked == true;
        toolbarVisibility.ShowAddExtensionButton = ShowAddExtensionButtonCheckBox.IsChecked == true;
        toolbarVisibility.ShowImportExtensionButton = ShowImportExtensionButtonCheckBox.IsChecked == true;
        toolbarVisibility.ShowManageExtensionsButton = ShowManageExtensionsButtonCheckBox.IsChecked == true;
        toolbarVisibility.ShowGestureSettingsButton = ShowGestureSettingsButtonCheckBox.IsChecked == true;
        toolbarVisibility.ShowSettingsButton = ShowSettingsButtonCheckBox.IsChecked == true;
        toolbarVisibility.ShowExtensionIcons = ShowExtensionIconsCheckBox.IsChecked == true;
        toolbarVisibility.ShowStatusText = ShowStatusTextCheckBox.IsChecked == true;

        if (!toolbarVisibility.ShowToolsButton && !toolbarVisibility.ShowSettingsButton)
        {
            SetValidationError(
                ShowToolsButtonCheckBox,
                "設定へ戻るため、ツールメニューか設定ボタンのどちらかは表示してください。");
            return false;
        }

        return true;
    }

    private bool TryReadDouble(
        TextBox textBox,
        string label,
        double minimum,
        double maximum,
        out double value)
    {
        var text = textBox.Text.Trim();
        if ((!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
                && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            || value < minimum
            || value > maximum)
        {
            SetValidationError(
                textBox,
                $"{label} は {minimum} から {maximum} の数値で指定してください。");
            return false;
        }

        return true;
    }

    private bool TryReadInt(
        TextBox textBox,
        string label,
        int minimum,
        int maximum,
        out int value)
    {
        var text = textBox.Text.Trim();
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out value)
            && !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            SetValidationError(
                textBox,
                $"{label} は {minimum} から {maximum} の整数で指定してください。");
            return false;
        }

        if (value < minimum || value > maximum)
        {
            SetValidationError(
                textBox,
                $"{label} は {minimum} から {maximum} の整数で指定してください。");
            return false;
        }

        return true;
    }

    private void UpdateGestureSummary()
    {
        var mappings = GestureSettingsCatalog.NormalizeMappings(_workingSettings.GestureMappings);
        GestureSummaryText.Text = $"{mappings.Count} 件の割り当て";
    }

    private void SetError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void SetValidationError(FrameworkElement target, string message)
    {
        SettingsTabControl.SelectedIndex = GetSettingsTabIndex(target);
        SetError(message);
        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                target.Focus();
                if (target is TextBox textBox)
                {
                    textBox.SelectAll();
                }
            }),
            DispatcherPriority.Input);
    }

    private int GetSettingsTabIndex(FrameworkElement target)
    {
        if (IsWithinSection(target, SearchSettingsSection)
            || IsWithinSection(target, BehaviorSettingsSection)
            || IsWithinSection(target, BrowserSettingsSection))
        {
            return GeneralSettingsTabIndex;
        }

        if (IsWithinSection(target, QuickLookSettingsSection)
            || IsWithinSection(target, ToolbarSettingsSection)
            || IsWithinSection(target, GestureSettingsSection))
        {
            return AppearanceSettingsTabIndex;
        }

        if (IsWithinSection(target, DownloadSettingsSection)

            )
        {
            return MediaSettingsTabIndex;
        }

        if (IsWithinSection(target, FiveChSettingsSection)
            || IsWithinSection(target, ChatGptSettingsSection))
        {
            return SiteSettingsTabIndex;
        }

        if (IsWithinSection(target, ClipboardHistorySettingsSection)
            || IsWithinSection(target, BrowsingHistorySettingsSection)
            || IsWithinSection(target, ContainerSettingsSection)

            )
        {
            return IntegrationSettingsTabIndex;
        }

        return SettingsTabControl.SelectedIndex;
    }

    private static bool IsWithinSection(FrameworkElement target, FrameworkElement section)
    {
        FrameworkElement? current = target;
        while (current is not null)
        {
            if (ReferenceEquals(current, section))
            {
                return true;
            }

            current = current.Parent as FrameworkElement;
        }

        return false;
    }

    private static AppSettings CloneSettings(AppSettings settings)
    {
        return new AppSettings
        {
            Version = settings.Version,
            SearchEngine = settings.SearchEngine,
            SearchUrlTemplate = settings.SearchUrlTemplate,
            SearchAliases = SearchAliasSettingsProfile.Normalize(settings.SearchAliases),
            GestureStartThreshold = settings.GestureStartThreshold,
            GestureDirectionThreshold = settings.GestureDirectionThreshold,
            PreserveContextMenuOnShortRightClick = settings.PreserveContextMenuOnShortRightClick,
            EnableLinkHoverPreview = settings.EnableLinkHoverPreview,
            GestureMappings = GestureSettingsCatalog.NormalizeMappings(settings.GestureMappings),
            EnableFocusedViewingCpuSaver = settings.EnableFocusedViewingCpuSaver,
            FocusedViewingCpuSaverDelaySeconds = settings.FocusedViewingCpuSaverDelaySeconds,
            EnableInactiveTabMemorySaver = settings.EnableInactiveTabMemorySaver,
            ClipboardHistory = ClipboardHistorySettingsProfile.Normalize(settings.ClipboardHistory),
            BrowsingHistory = BrowsingHistorySettingsProfile.Normalize(settings.BrowsingHistory),
            SitePrivacy = SitePrivacySettingsProfile.Normalize(settings.SitePrivacy),
            EnableBrowserExtensions = settings.EnableBrowserExtensions,

            PreservedCompatibilitySettings = PublicSettingsCompatibility.Clone(settings.PreservedCompatibilitySettings),

            EnableXReadableMode = settings.EnableXReadableMode,
            ChatGptReadability = CloneChatGptReadability(settings.ChatGptReadability),
            FiveChLiveReadability = CloneFiveChLiveReadability(settings.FiveChLiveReadability),
            AlwaysOnTop = settings.AlwaysOnTop,

            Downloads = DownloadSettingsProfile.Normalize(settings.Downloads),
            TabLayout = CloneTabLayout(settings.TabLayout),
            FolderTabViewMode = FolderTabViewMode.Normalize(settings.FolderTabViewMode),
            FolderTabPinnedPlaces = FolderTabPlacesService.NormalizePinnedPlaces(
                settings.FolderTabPinnedPlaces),
            ShowHiddenFolderTabItems = settings.ShowHiddenFolderTabItems,
            QuickLook = QuickLookSettingsProfile.Normalize(settings.QuickLook),
            EnableYouTubeMiniPlayerHoverAudioPreview =
                settings.EnableYouTubeMiniPlayerHoverAudioPreview,
            EnableYouTubeMiniPlayerSizeBasedQuality =
                settings.EnableYouTubeMiniPlayerSizeBasedQuality,
            YouTubeMiniPlayerQualityLowWidthThreshold =
                settings.YouTubeMiniPlayerQualityLowWidthThreshold,
            YouTubeMiniPlayerQualityHighWidthThreshold =
                settings.YouTubeMiniPlayerQualityHighWidthThreshold,
            WindowPlacement = settings.WindowPlacement is null
                ? null
                : new WindowPlacementSettings
                {
                    Left = settings.WindowPlacement.Left,
                    Top = settings.WindowPlacement.Top,
                    Width = settings.WindowPlacement.Width,
                    Height = settings.WindowPlacement.Height,
                    State = settings.WindowPlacement.State
                },
            BookmarkManagerWindowSize = settings.BookmarkManagerWindowSize is null
                ? null
                : new BookmarkManagerWindowSizeSettings
                {
                    Width = settings.BookmarkManagerWindowSize.Width,
                    Height = settings.BookmarkManagerWindowSize.Height
                },
            WorkspaceNoteEditorWindowPlacement = WorkspaceNoteEditorWindowPlacementProfile.Normalize(
                settings.WorkspaceNoteEditorWindowPlacement),
            WorkspaceFolderWindowPlacements = WorkspaceFolderWindowPlacementProfile.Normalize(
                settings.WorkspaceFolderWindowPlacements),
            LastWorkspaceId = settings.LastWorkspaceId,
            ToolbarVisibility = CloneToolbarVisibility(settings.ToolbarVisibility)
        };
    }

    private static TabLayoutSettings CloneTabLayout(TabLayoutSettings? settings)
    {
        settings = TabLayoutSettingsProfile.Normalize(settings);
        return new TabLayoutSettings
        {
            Mode = settings.Mode,
            VerticalSidebarWidth = settings.VerticalSidebarWidth,
            VerticalSidebarCollapsed = settings.VerticalSidebarCollapsed,
            ShowTabSearch = settings.ShowTabSearch,
            ShowPinnedTabsSection = settings.ShowPinnedTabsSection
        };
    }

    private static ChatGptReadabilitySettings CloneChatGptReadability(ChatGptReadabilitySettings? settings)
    {
        settings = ChatGptReadabilityProfile.Normalize(settings);
        return new ChatGptReadabilitySettings
        {
            IsEnabled = settings.IsEnabled,
            FontScalePercent = settings.FontScalePercent,
            LineHeight = settings.LineHeight,
            Theme = settings.Theme,
            ContentWidth = settings.ContentWidth,
            CodeFontScalePercent = settings.CodeFontScalePercent,
            WrapCodeBlocks = settings.WrapCodeBlocks,
            LargeInput = settings.LargeInput,
            HighlightKeyboardFocus = settings.HighlightKeyboardFocus
        };
    }

    private static FiveChLiveReadabilitySettings CloneFiveChLiveReadability(FiveChLiveReadabilitySettings? settings)
    {
        return FiveChLiveReadabilityProfile.Normalize(settings);
    }

    private void ApplyFiveChLiveReadabilityToInputs(FiveChLiveReadabilitySettings? settings)
    {
        settings = FiveChLiveReadabilityProfile.Normalize(settings);
        EnableFiveChLiveReadabilityCheckBox.IsChecked = settings.IsEnabled;
        HighlightFiveChNewPostsCheckBox.IsChecked = settings.HighlightNewPosts;
        EnableFiveChAutoRefreshCheckBox.IsChecked = settings.AutoRefresh;
        FiveChRefreshIntervalBox.Text = settings.RefreshIntervalSeconds.ToString(CultureInfo.CurrentCulture);
        EnableFiveChAutoOpenNextThreadCheckBox.IsChecked = settings.AutoOpenNextThread;
        EnableFiveChAutoScrollCheckBox.IsChecked = settings.AutoScroll;
        PreviewFiveChReplyAnchorsCheckBox.IsChecked = settings.PreviewReplyAnchors;
        HighlightFiveChPosterIdsCheckBox.IsChecked = settings.HighlightPosterIds;
        FiveChFontSizeBox.Text = settings.FontSize.ToString(CultureInfo.CurrentCulture);
        FiveChPostSpacingBox.Text = settings.PostSpacing.ToString(CultureInfo.CurrentCulture);
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

    private static string NormalizeCliPath(string value)
    {
        return value.Trim().Trim('"');
    }
}
