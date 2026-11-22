using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TabBrowser.Services;
using TabBrowser.Views;

namespace TabBrowser;

public partial class MainWindow
{
    private YouTubeMiniPlayerArrangementAreaMode _youTubeMiniPlayerArrangementAreaMode;
    private SelectedArrangementRegion? _youTubeMiniPlayerSelectedArrangementRegion;
    private YouTubeMiniPlayerArrangementRegionOutlineWindow? _youTubeMiniPlayerArrangementRegionOutlineWindow;
    private int _youTubeMiniPlayerArrangementRegionGeneration;

    private sealed record SelectedArrangementRegion(Rect Bounds, IReadOnlyList<Rect> WorkAreas);

    private YouTubeMiniPlayerArrangementDisplay? ResolveYouTubeMiniPlayerArrangementDisplay(
        IReadOnlyList<YouTubeMiniPlayerArrangementDisplay>? displays = null)
    {
        displays ??= YouTubeMiniPlayerArrangementDisplayService.GetDisplays();
        var key = YouTubeMiniPlayerArrangeDisplayComboBox.SelectedValue as string;
        if (!string.IsNullOrEmpty(key)) return displays.FirstOrDefault(display => string.Equals(display.DeviceName, key, StringComparison.OrdinalIgnoreCase));
        var dpi = VisualTreeHelper.GetDpi(this);
        var current = GetActiveDisplayWorkArea();
        var center = new Point((current.Left + current.Width / 2) * dpi.DpiScaleX, (current.Top + current.Height / 2) * dpi.DpiScaleY);
        return displays.FirstOrDefault(display => display.WorkArea.Contains(center));
    }

    private void YouTubeMiniPlayerArrangeAreaComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (YouTubeMiniPlayerArrangeAreaComboBox.SelectedItem is not ComboBoxItem { Tag: string tag }
            || !Enum.TryParse<YouTubeMiniPlayerArrangementAreaMode>(tag, out var mode)
            || mode == _youTubeMiniPlayerArrangementAreaMode) return;
        _youTubeMiniPlayerArrangementAreaMode = mode;
        ClearYouTubeMiniPlayerArrangeMenuFailure();
        CancelYouTubeMiniPlayerArrangementPreview(showStatus: false);
        UpdateYouTubeMiniPlayerArrangementAreaText();
        if (mode == YouTubeMiniPlayerArrangementAreaMode.SelectedRegion && YouTubeMiniPlayerArrangePopup.IsOpen)
            ShowYouTubeMiniPlayerArrangementRegionOutline();
        else
            CloseYouTubeMiniPlayerArrangementRegionOutline();
    }

    private void YouTubeMiniPlayerArrangeIncludeMinimizedCheckBox_Click(object sender, RoutedEventArgs e)
        => ClearYouTubeMiniPlayerArrangeMenuFailure();

    private void YouTubeMiniPlayerArrangeRemoveSpacingCheckBox_Click(object sender, RoutedEventArgs e)
    {
        ClearYouTubeMiniPlayerArrangeMenuFailure();
        CancelYouTubeMiniPlayerArrangementPreview(showStatus: false);
    }

    private void ClearYouTubeMiniPlayerArrangeMenuFailure()
        => SetYouTubeMiniPlayerArrangeMenuFailure(null);

    private void SetYouTubeMiniPlayerArrangeMenuFailure(string? text)
    {
        if (YouTubeMiniPlayerArrangeFailurePanel is null) return;
        var visible = text is not null && YouTubeMiniPlayerArrangePopup.IsOpen;
        YouTubeMiniPlayerArrangeFailureText.Text = visible ? text : string.Empty;
        YouTubeMiniPlayerArrangeFailurePanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateYouTubeMiniPlayerArrangementAreaText()
    {
        YouTubeMiniPlayerArrangeRegionButton.IsEnabled = _youTubeMiniPlayerArrangementAreaMode == YouTubeMiniPlayerArrangementAreaMode.SelectedRegion;
        YouTubeMiniPlayerArrangeAreaText.Text = _youTubeMiniPlayerArrangementAreaMode switch
        {
            YouTubeMiniPlayerArrangementAreaMode.FreeSpace => "他のアプリ・対象外のプレイヤーを避け、1か所に配置します",
            YouTubeMiniPlayerArrangementAreaMode.SelectedRegion when _youTubeMiniPlayerSelectedArrangementRegion is { } region
                => $"指定範囲：{region.Bounds.Width:0} × {region.Bounds.Height:0} px。下の配置方式を選んでください",
            YouTubeMiniPlayerArrangementAreaMode.SelectedRegion => "「範囲を指定」でドラッグしてください。ディスプレイをまたげます",
            _ => "画面の作業領域を使います"
        };
    }

    private void ClearYouTubeMiniPlayerArrangementRegion()
    {
        CancelYouTubeMiniPlayerArrangementRegion();
        _youTubeMiniPlayerSelectedArrangementRegion = null;
        UpdateYouTubeMiniPlayerArrangementAreaText();
    }

    private void RefreshYouTubeMiniPlayerArrangementRegion()
    {
        if (_youTubeMiniPlayerSelectedArrangementRegion is { } selected
            && !selected.WorkAreas.SequenceEqual(GetYouTubeMiniPlayerRegionWorkAreas(selected.Bounds,
                YouTubeMiniPlayerArrangementDisplayService.GetDisplays())))
        {
            ClearYouTubeMiniPlayerArrangementRegion();
        }
        UpdateYouTubeMiniPlayerArrangementAreaText();
    }

    private static IReadOnlyList<Rect> GetYouTubeMiniPlayerRegionWorkAreas(
        Rect region, IReadOnlyList<YouTubeMiniPlayerArrangementDisplay> displays)
        => YouTubeMiniPlayerArrangementAreaService.GetIntersectingWorkAreas(region, displays.Select(display => display.WorkArea));

    private void CancelYouTubeMiniPlayerArrangementRegion()
    {
        _youTubeMiniPlayerArrangementRegionGeneration++;
        _youTubeMiniPlayerArrangementRegionWindow?.Cancel();
        CloseYouTubeMiniPlayerArrangementRegionOutline();
    }

    private void ShowYouTubeMiniPlayerArrangementRegionOutline()
    {
        if (_isClosingWindow || !_youTubeMiniPlayerListWindow.IsOpen || _youTubeMiniPlayerListWindow.IsCollapsed
            || _youTubeMiniPlayerArrangementAreaMode != YouTubeMiniPlayerArrangementAreaMode.SelectedRegion
            || _youTubeMiniPlayerSelectedArrangementRegion is not { } region
            || _youTubeMiniPlayerArrangementRegionOutlineWindow is not null) return;
        var outline = new YouTubeMiniPlayerArrangementRegionOutlineWindow(region.Bounds, VisualTreeHelper.GetDpi(this))
        { Owner = _youTubeMiniPlayerListWindow };
        _youTubeMiniPlayerArrangementRegionOutlineWindow = outline;
        outline.Closed += (_, _) =>
        {
            if (ReferenceEquals(_youTubeMiniPlayerArrangementRegionOutlineWindow, outline))
                _youTubeMiniPlayerArrangementRegionOutlineWindow = null;
        };
        try
        {
            outline.Show();
        }
        catch (InvalidOperationException)
        {
            CloseYouTubeMiniPlayerArrangementRegionOutline();
        }
    }

    private void CloseYouTubeMiniPlayerArrangementRegionOutline()
    {
        if (_youTubeMiniPlayerArrangementRegionOutlineWindow is not { } outline) return;
        _youTubeMiniPlayerArrangementRegionOutlineWindow = null;
        try
        {
            outline.Close();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void YouTubeMiniPlayerArrangeRegionButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_isClosingWindow || _youTubeMiniPlayerArrangementRegionWindow is not null) return;
        var displays = YouTubeMiniPlayerArrangementDisplayService.GetDisplays();
        var desktop = YouTubeMiniPlayerArrangementAreaService.GetDesktopBounds(displays.Select(display => display.WorkArea));
        if (!YouTubeMiniPlayerArrangementAreaService.IsValid(desktop))
        {
            SetYouTubeMiniPlayerStatus("ディスプレイの配置を確認できません。範囲を指定し直してください", autoClear: true);
            return;
        }
        CancelYouTubeMiniPlayerArrangementPreview(showStatus: false);
        CloseYouTubeMiniPlayerArrangementRegionOutline();
        YouTubeMiniPlayerArrangePopup.IsOpen = false;
        YouTubeMiniPlayerLinearPlacementPopup.IsOpen = false;
        var generation = ++_youTubeMiniPlayerArrangementRegionGeneration;
        var instructionsArea = ResolveYouTubeMiniPlayerArrangementDisplay(displays)?.WorkArea ?? displays[0].WorkArea;
        var selector = new YouTubeMiniPlayerArrangementRegionWindow(desktop, VisualTreeHelper.GetDpi(this), instructionsArea)
        { Owner = _youTubeMiniPlayerListWindow };
        _youTubeMiniPlayerArrangementRegionWindow = selector;
        selector.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_youTubeMiniPlayerArrangementRegionWindow, selector)) return;
            _youTubeMiniPlayerArrangementRegionWindow = null;
            if (generation != _youTubeMiniPlayerArrangementRegionGeneration || _isClosingWindow) return;
            if (selector.SelectedPhysicalBounds is { } bounds)
            {
                var workAreas = GetYouTubeMiniPlayerRegionWorkAreas(bounds, displays);
                var confirmed = workAreas.Count > 0 && workAreas.SequenceEqual(GetYouTubeMiniPlayerRegionWorkAreas(bounds,
                    YouTubeMiniPlayerArrangementDisplayService.GetDisplays()));
                _youTubeMiniPlayerSelectedArrangementRegion = confirmed ? new(bounds, workAreas) : null;
                UpdateYouTubeMiniPlayerArrangementAreaText();
                SetYouTubeMiniPlayerStatus(confirmed
                    ? "範囲を確定しました。整列メニューで配置方式を選んでください"
                    : "ディスプレイが変わったため、範囲を指定し直してください", autoClear: true);
            }
            if (!selector.ReturnToMenu) return;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (_isClosingWindow || generation != _youTubeMiniPlayerArrangementRegionGeneration
                    || !_youTubeMiniPlayerListWindow.IsOpen || !_youTubeMiniPlayerListWindow.IsActive
                    || _youTubeMiniPlayerListWindow.IsCollapsed
                    || _youTubeMiniPlayerArrangementRegionWindow is not null) return;
                UpdateYouTubeMiniPlayerArrangePopupPlacement();
                YouTubeMiniPlayerArrangePopup.IsOpen = true;
                YouTubeMiniPlayerArrangeRegionButton.Focus();
            }));
        };
        try { selector.Show(); }
        catch (Exception ex)
        {
            selector.Cancel();
            SetYouTubeMiniPlayerStatus("範囲選択を開始できませんでした: " + ex.Message, autoClear: true);
        }
    }

    private YouTubeMiniPlayerArrangementAreaRequest? CaptureYouTubeMiniPlayerPlacementArea(
        IReadOnlyList<Guid> targetIds, YouTubeMiniPlayerArrangeMode mode)
    {
        var areaMode = _youTubeMiniPlayerArrangementAreaMode;
        if (areaMode == YouTubeMiniPlayerArrangementAreaMode.Display) return null;
        var dpi = VisualTreeHelper.GetDpi(this);
        if (areaMode == YouTubeMiniPlayerArrangementAreaMode.SelectedRegion)
        {
            var selected = _youTubeMiniPlayerSelectedArrangementRegion;
            if (selected is null) return new(areaMode, "", null, []);
            var valid = selected.WorkAreas.SequenceEqual(GetYouTubeMiniPlayerRegionWorkAreas(selected.Bounds,
                YouTubeMiniPlayerArrangementDisplayService.GetDisplays()));
            return new(areaMode, "", YouTubeMiniPlayerArrangementAreaService.ToHostCoordinates(selected.Bounds, dpi), [], valid)
            {
                HostDpi = dpi,
                OutsideWorkAreas = YouTubeMiniPlayerArrangementAreaService.GetOutsideWorkAreas(selected.Bounds, selected.WorkAreas)
                    .Select(bounds => YouTubeMiniPlayerArrangementAreaService.ToHostCoordinates(bounds, dpi)).ToArray()
            };
        }

        var display = ResolveYouTubeMiniPlayerArrangementDisplay();
        if (display is null) return new(areaMode, "", null, [], false);
        var excluded = new HashSet<IntPtr>();
        foreach (var owner in GetYouTubeMiniPlayerOwnerWindows())
        {
            AddWindow(owner._youTubeMiniPlayerArrangementPreviewWindow);
            AddWindow(owner._youTubeMiniPlayerArrangementRegionWindow);
            AddWindow(owner._youTubeMiniPlayerArrangementRegionOutlineWindow);
            AddPopup(owner.YouTubeMiniPlayerArrangePopup);
            AddPopup(owner.YouTubeMiniPlayerLinearPlacementPopup);
            foreach (var id in targetIds)
            {
                if (!owner._youTubeMiniPlayerRuntimes.TryGetValue(id, out var runtime)) continue;
                AddWindow(runtime.TaskbarWindow);
                AddPopup(runtime.Popup);
                AddPopup(runtime.TitleBandPopup);
                AddPopup(runtime.ToolbarPopup);
                if (owner._youTubeLiveChatController.TryGetWindow(id, out var chat)
                    && (mode == YouTubeMiniPlayerArrangeMode.Chat
                        || (runtime.State.IsLiveChatWindowDocked && chat.WindowState != WindowState.Minimized))) AddWindow(chat);
            }
        }
        var captured = YouTubeMiniPlayerOccupiedAreaService.TryCapture(excluded, dpi, out var obstacles);
        return new(areaMode, display.DeviceName, null, obstacles, captured);

        void AddWindow(Window? window)
        {
            if (window is not null && new WindowInteropHelper(window).Handle is var handle && handle != IntPtr.Zero) excluded.Add(handle);
        }
        void AddPopup(Popup popup)
        {
            if (popup.Child is { } child && PresentationSource.FromVisual(child) is HwndSource source) excluded.Add(source.Handle);
        }
    }
}
