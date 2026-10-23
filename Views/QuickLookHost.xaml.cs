using System.IO;
using System.ComponentModel;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using TabBrowser.Models;
using TabBrowser.Models.QuickLook;
using TabBrowser.Services.QuickLook;

namespace TabBrowser.Views;

public readonly record struct QuickLookHostDiagnosticSnapshot(
    bool IsOpen,
    bool IsDisposed,
    bool HasRequest,
    bool IsClosePending,
    bool HasInteractiveContent,
    QuickLookServiceDiagnosticSnapshot Service)
{
    public bool HasHiddenActiveLoad => !IsOpen && Service.HasActiveLoad;

    public bool HasActivityAfterDispose =>
        IsDisposed
        && (IsOpen
            || HasRequest
            || HasInteractiveContent
            || Service.HasActiveLoad
            || Service.HasCurrentRenderer);
}

public partial class QuickLookHost : UserControl, IQuickLookHost
{
    private const string PaneDragDataFormat = "TabBrowser.QuickLookPane";
    private const double FloatingHorizontalOuterMargin = 28;
    private const double FloatingVerticalOuterMargin = 42;
    private static readonly Brush SelectedControlBackground = new SolidColorBrush(Color.FromRgb(29, 78, 216));
    private static readonly Brush SelectedControlBorder = new SolidColorBrush(Color.FromRgb(96, 165, 250));
    private static readonly Brush NormalControlBackground = new SolidColorBrush(Color.FromRgb(36, 42, 52));
    private static readonly Brush NormalControlBorder = new SolidColorBrush(Color.FromRgb(75, 85, 99));

    private QuickLookService _service;
    private QuickLookRequest? _request;
    private Task _closeTask = Task.CompletedTask;
    private int _loadVersion;
    private bool _isDisposed;
    private bool _useRightSidePane;
    private string _panePlacement = QuickLookPanePlacement.Right;
    private double _rightPaneWidth = QuickLookSettingsProfile.DefaultRightPaneWidth;
    private double _bottomPaneHeight = QuickLookSettingsProfile.DefaultBottomPaneHeight;
    private double _floatingOffsetX;
    private double _floatingOffsetY;
    private double _floatingWidth = QuickLookSettingsProfile.DefaultFloatingWidth;
    private double _floatingHeight = QuickLookSettingsProfile.DefaultFloatingHeight;
    private int _textFontSize = QuickLookSettingsProfile.DefaultTextFontSize;
    private bool _textWrap = true;
    private FrameworkElement? _dockTarget;
    private Thickness _dockTargetBaseMargin;
    private Point? _paneDragStart;
    private Point? _floatingDragStart;
    private double _floatingDragStartOffsetX;
    private double _floatingDragStartOffsetY;
    private bool _paneSizeChanged;
    private bool _floatingPositionChanged;
    private bool _floatingSizeChanged;

    public QuickLookHost()
    {
        InitializeComponent();
        _service = new QuickLookService(QuickLookSettingsProfile.Normalize(null));
        SizeChanged += (_, _) => UpdateLayoutForCurrentMode();
    }

    public bool IsOpen => Visibility == Visibility.Visible;

    public QuickLookHostDiagnosticSnapshot GetDiagnosticSnapshot()
    {
        return new QuickLookHostDiagnosticSnapshot(
            IsOpen,
            _isDisposed,
            _request is not null,
            !_closeTask.IsCompleted,
            InteractiveContent.Content is not null,
            _service.GetDiagnosticSnapshot());
    }

    public event EventHandler<QuickLookPaneLayoutChangedEventArgs>? PaneLayoutChanged;

    public event EventHandler<QuickLookTextDisplayChangedEventArgs>? TextDisplayChanged;

    public void Configure(QuickLookSettings settings)
    {
        if (_isDisposed)
        {
            return;
        }

        var normalized = QuickLookSettingsProfile.Normalize(settings);
        Close();
        _service.Dispose();
        _service = new QuickLookService(normalized);
        _useRightSidePane = normalized.UseRightSidePane;
        _panePlacement = normalized.PanePlacement;
        _rightPaneWidth = normalized.RightPaneWidth;
        _bottomPaneHeight = normalized.BottomPaneHeight;
        _floatingOffsetX = normalized.FloatingOffsetX;
        _floatingOffsetY = normalized.FloatingOffsetY;
        _floatingWidth = normalized.FloatingWidth;
        _floatingHeight = normalized.FloatingHeight;
        _textFontSize = normalized.TextFontSize;
        _textWrap = normalized.TextWrap;
        ApplyDisplayMode();
        ApplyTextDisplaySettings();
    }

    public void UpdatePaneLayout(QuickLookSettings settings)
    {
        if (_isDisposed)
        {
            return;
        }

        var normalized = QuickLookSettingsProfile.Normalize(settings);
        _useRightSidePane = normalized.UseRightSidePane;
        _panePlacement = normalized.PanePlacement;
        _rightPaneWidth = normalized.RightPaneWidth;
        _bottomPaneHeight = normalized.BottomPaneHeight;
        _floatingOffsetX = normalized.FloatingOffsetX;
        _floatingOffsetY = normalized.FloatingOffsetY;
        _floatingWidth = normalized.FloatingWidth;
        _floatingHeight = normalized.FloatingHeight;
        _textFontSize = normalized.TextFontSize;
        _textWrap = normalized.TextWrap;
        ApplyDisplayMode();
        ApplyTextDisplaySettings();
    }

    public void ConfigureDockTarget(FrameworkElement dockTarget)
    {
        if (_isDisposed || ReferenceEquals(_dockTarget, dockTarget))
        {
            return;
        }

        RestoreDockTargetMargin();
        _dockTarget = dockTarget;
        _dockTargetBaseMargin = dockTarget.Margin;
        ApplyDockTargetLayout();
    }

    public async Task ShowAsync(
        QuickLookRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_isDisposed || request.Items.Count == 0)
        {
            return;
        }

        await _closeTask;
        // FolderTab・Workspace・パレットの共通入口で、表示元Windowの文脈を渡す。
        for (var owner = Window.GetWindow(this); owner is not null; owner = owner.Owner)
        {
            if (owner is MainWindow main)
            {
                request = request with { IsPrivate = request.IsPrivate || main.IsPrivateBrowsing };
                break;
            }
        }
        var loadVersion = ++_loadVersion;
        _request = request;
        Visibility = Visibility.Visible;
        ApplyDockTargetLayout();
        QuickLookRenderResult? immediateResult = null;
        if (QuickLookWebSummaryRenderer.TryCreateImmediateResult(request, out immediateResult)
            && immediateResult is not null)
        {
            ShowResult(immediateResult);
        }
        else
        {
            ShowLoading(request.PrimaryItem?.DisplayName ?? "QuickLook");
        }

        UpdateButtons();
        try
        {
            var result = immediateResult is null
                ? await _service.LoadAsync(request, cancellationToken)
                : await _service.LoadWithImmediateResultAsync(
                    request,
                    immediateResult,
                    cancellationToken);
            if (!_isDisposed
                && loadVersion == _loadVersion
                && IsOpen
                && !Equals(result, immediateResult))
            {
                ShowResult(result);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Close()
    {
        if (_isDisposed)
        {
            return;
        }

        _loadVersion++;
        Visibility = Visibility.Collapsed;
        ApplyDockTargetLayout();
        _request = null;
        ClearContent();
        _closeTask = _service.CloseAsync().AsTask();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _loadVersion++;
        Visibility = Visibility.Collapsed;
        RestoreDockTargetMargin();
        _request = null;
        ClearContent();
        _isDisposed = true;
        _service.Dispose();
    }

    private async void QuickLookHost_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && OpenButton.IsEnabled)
        {
            await OpenCurrentItemAsync();
            e.Handled = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void FloatingModeButton_Click(object sender, RoutedEventArgs e)
    {
        SetDisplayMode(usePane: false, _panePlacement);
    }

    private void RightPaneModeButton_Click(object sender, RoutedEventArgs e)
    {
        SetDisplayMode(usePane: true, QuickLookPanePlacement.Right);
    }

    private void BottomPaneModeButton_Click(object sender, RoutedEventArgs e)
    {
        SetDisplayMode(usePane: true, QuickLookPanePlacement.Bottom);
    }

    private void TextSmallerButton_Click(object sender, RoutedEventArgs e)
    {
        SetTextFontSize(_textFontSize - 1);
    }

    private void TextLargerButton_Click(object sender, RoutedEventArgs e)
    {
        SetTextFontSize(_textFontSize + 1);
    }

    private void TextWrapButton_Click(object sender, RoutedEventArgs e)
    {
        _textWrap = !_textWrap;
        ApplyTextDisplaySettings();
        TextDisplayChanged?.Invoke(this, new QuickLookTextDisplayChangedEventArgs(_textFontSize, _textWrap));
    }

    private void PaneDragHeader_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_useRightSidePane)
        {
            _paneDragStart = e.GetPosition(PaneDragHeader);
            return;
        }

        _floatingDragStart = e.GetPosition(HostRoot);
        _floatingDragStartOffsetX = _floatingOffsetX;
        _floatingDragStartOffsetY = _floatingOffsetY;
        _floatingPositionChanged = false;
        _ = PaneDragHeader.CaptureMouse();
        e.Handled = true;
    }

    private void PaneDragHeader_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _paneDragStart = null;
        CompleteFloatingDrag();
    }

    private void PaneDragHeader_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_useRightSidePane)
        {
            UpdateFloatingPositionFromDrag(e);
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed || _paneDragStart is not { } start)
        {
            return;
        }

        var current = e.GetPosition(PaneDragHeader);
        if (Math.Abs(current.X - start.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _paneDragStart = null;
        var data = new DataObject(PaneDragDataFormat, this);
        var background = HostRoot.Background;
        try
        {
            HostRoot.Background = System.Windows.Media.Brushes.Transparent;
            _ = DragDrop.DoDragDrop(PaneDragHeader, data, DragDropEffects.Move);
        }
        finally
        {
            HostRoot.Background = background;
        }

        e.Handled = true;
    }

    private void PaneDragHeader_LostMouseCapture(object sender, MouseEventArgs e)
    {
        CompleteFloatingDrag(releaseCapture: false);
    }

    private void UpdateFloatingPositionFromDrag(MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed
            || _floatingDragStart is not { } start
            || !PaneDragHeader.IsMouseCaptured)
        {
            return;
        }

        var current = e.GetPosition(HostRoot);
        var deltaX = current.X - start.X;
        var deltaY = current.Y - start.Y;
        if (!_floatingPositionChanged
            && Math.Abs(deltaX) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(deltaY) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _floatingPositionChanged = true;
        _floatingOffsetX = _floatingDragStartOffsetX + deltaX;
        _floatingOffsetY = _floatingDragStartOffsetY + deltaY;
        ApplyFloatingPosition();
        e.Handled = true;
    }

    private void CompleteFloatingDrag(bool releaseCapture = true)
    {
        if (_floatingDragStart is null)
        {
            return;
        }

        _floatingDragStart = null;
        if (releaseCapture && PaneDragHeader.IsMouseCaptured)
        {
            PaneDragHeader.ReleaseMouseCapture();
        }

        if (_floatingPositionChanged)
        {
            _floatingPositionChanged = false;
            NotifyPaneLayoutChanged();
        }
    }

    private void HostRoot_DragOver(object sender, DragEventArgs e)
    {
        if (!_useRightSidePane || !IsOwnPaneDrag(e.Data))
        {
            return;
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void HostRoot_Drop(object sender, DragEventArgs e)
    {
        if (!_useRightSidePane || !IsOwnPaneDrag(e.Data))
        {
            return;
        }

        var placement = ResolvePanePlacement(e.GetPosition(HostRoot));
        if (!string.Equals(_panePlacement, placement, StringComparison.Ordinal))
        {
            _panePlacement = placement;
            ApplyDisplayMode();
            NotifyPaneLayoutChanged();
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void RightResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (!_useRightSidePane
            || !string.Equals(_panePlacement, QuickLookPanePlacement.Right, StringComparison.Ordinal))
        {
            return;
        }

        var width = Math.Clamp(
            _rightPaneWidth - e.HorizontalChange,
            QuickLookSettingsProfile.MinimumRightPaneWidth,
            QuickLookSettingsProfile.MaximumRightPaneWidth);
        _paneSizeChanged |= Math.Abs(width - _rightPaneWidth) >= 0.5;
        _rightPaneWidth = width;
        UpdatePaneSize();
        e.Handled = true;
    }

    private void BottomResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (!_useRightSidePane
            || !string.Equals(_panePlacement, QuickLookPanePlacement.Bottom, StringComparison.Ordinal))
        {
            return;
        }

        var height = Math.Clamp(
            _bottomPaneHeight - e.VerticalChange,
            QuickLookSettingsProfile.MinimumBottomPaneHeight,
            QuickLookSettingsProfile.MaximumBottomPaneHeight);
        _paneSizeChanged |= Math.Abs(height - _bottomPaneHeight) >= 0.5;
        _bottomPaneHeight = height;
        UpdatePaneSize();
        e.Handled = true;
    }

    private void FloatingResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_useRightSidePane)
        {
            return;
        }

        var availableWidth = GetFloatingAvailableDimension(ActualWidth, FloatingHorizontalOuterMargin);
        var availableHeight = GetFloatingAvailableDimension(ActualHeight, FloatingVerticalOuterMargin);
        var currentWidth = ConstrainFloatingDimension(
            _floatingWidth,
            availableWidth,
            QuickLookSettingsProfile.MinimumFloatingWidth,
            QuickLookSettingsProfile.MaximumFloatingWidth);
        var currentHeight = ConstrainFloatingDimension(
            _floatingHeight,
            availableHeight,
            QuickLookSettingsProfile.MinimumFloatingHeight,
            QuickLookSettingsProfile.MaximumFloatingHeight);
        var width = ConstrainFloatingDimension(
            currentWidth + e.HorizontalChange,
            availableWidth,
            QuickLookSettingsProfile.MinimumFloatingWidth,
            QuickLookSettingsProfile.MaximumFloatingWidth);
        var height = ConstrainFloatingDimension(
            currentHeight + e.VerticalChange,
            availableHeight,
            QuickLookSettingsProfile.MinimumFloatingHeight,
            QuickLookSettingsProfile.MaximumFloatingHeight);
        var widthChange = width - currentWidth;
        var heightChange = height - currentHeight;
        if (Math.Abs(widthChange) < 0.5 && Math.Abs(heightChange) < 0.5)
        {
            return;
        }

        _floatingSizeChanged = true;
        _floatingWidth = width;
        _floatingHeight = height;
        _floatingOffsetX += widthChange / 2;
        _floatingOffsetY += heightChange / 2;
        ApplyFloatingSize();
        e.Handled = true;
    }

    private void PaneResizeThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_paneSizeChanged || _floatingSizeChanged)
        {
            _paneSizeChanged = false;
            _floatingSizeChanged = false;
            NotifyPaneLayoutChanged();
        }
    }

    private async void PreviousButton_Click(object sender, RoutedEventArgs e)
    {
        if (_request?.ItemSource is not { } source || source.MovePrevious() is null)
        {
            return;
        }

        await ShowFromSourceAsync(source);
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_request?.ItemSource is not { } source || source.MoveNext() is null)
        {
            return;
        }

        await ShowFromSourceAsync(source);
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        await OpenCurrentItemAsync();
    }

    private async void QuickLookActionButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is QuickLookAction action)
        {
            await ExecuteQuickLookActionAsync(action);
        }
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_request?.Items is not [var item]
            || GetCopyText(item) is not { Length: > 0 } text)
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            ShowOperationError(ex.Message);
        }
    }

    private void ShowInFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_request?.Items is not [var item])
        {
            return;
        }

        try
        {
            _service.ShowInFolder(item);
        }
        catch (Exception ex) when (IsExpectedShellOperationException(ex))
        {
            ShowOperationError(ex.Message);
        }
    }

    private async Task ShowFromSourceAsync(IQuickLookItemSource source)
    {
        var selected = source.SelectedItems;
        var items = selected.Count > 0
            ? selected
            : source.Current is { } current ? [current] : [];
        if (items.Count > 0)
        {
            await ShowAsync(new QuickLookRequest(items, source.SourceId, source));
        }
    }

    private async Task OpenCurrentItemAsync()
    {
        if (_request?.Items is not [var item])
        {
            return;
        }

        if (item.Actions.FirstOrDefault(static action => action.IsPrimary) is { } primaryAction)
        {
            await ExecuteQuickLookActionAsync(primaryAction);
            return;
        }

        try
        {
            _service.OpenWithDefaultApplication(item);
        }
        catch (Exception ex) when (IsExpectedShellOperationException(ex))
        {
            ShowOperationError(ex.Message);
        }
    }

    private async Task ExecuteQuickLookActionAsync(QuickLookAction action)
    {
        try
        {
            await action.ExecuteAsync();
        }
        catch (Exception ex)
        {
            ShowOperationError(ex.Message);
        }
    }

    private static string? GetCopyText(QuickLookItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.InlineText))
        {
            return item.InlineText;
        }

        if (!string.IsNullOrWhiteSpace(item.Uri))
        {
            return item.Uri;
        }

        if (!string.IsNullOrWhiteSpace(item.FilePath))
        {
            return item.FilePath;
        }

        return string.IsNullOrWhiteSpace(item.Description)
            ? null
            : item.Description;
    }

    private void ShowLoading(string title)
    {
        ClearContent();
        TitleText.Text = title;
        SubtitleText.Text = "読み込み中";
        StateText.Text = "プレビューを読み込んでいます...";
        StateText.Visibility = Visibility.Visible;
    }

    private void ShowResult(QuickLookRenderResult result)
    {
        ClearContent();
        TitleText.Text = result.Title;
        SubtitleText.Text = result.Subtitle;
        LimitText.Visibility = result.IsTruncated ? Visibility.Visible : Visibility.Collapsed;
        switch (result.Kind)
        {
            case QuickLookRenderKind.WebSummary:
                var hasWebSummaryImage = result.PreviewImage is not null;
                var hasWebSummaryFavicon = result.Icon is not null;
                WebSummaryImage.Source = result.PreviewImage;
                WebSummaryImage.Visibility = hasWebSummaryImage
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                WebSummaryImageFallbackPanel.Visibility = hasWebSummaryImage
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                WebSummaryFavicon.Source = result.Icon;
                WebSummaryFavicon.Visibility = hasWebSummaryFavicon
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                WebSummaryFaviconFallbackText.Visibility = hasWebSummaryFavicon
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                WebSummaryHeroFavicon.Source = result.Icon;
                WebSummaryHeroFavicon.Visibility = hasWebSummaryFavicon
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                WebSummaryHeroFaviconFallbackText.Visibility = hasWebSummaryFavicon
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                WebSummaryTitleText.Text = result.Title;
                WebSummaryDomainText.Text = string.IsNullOrWhiteSpace(result.TypeLabel)
                    ? result.Subtitle
                    : $"{result.TypeLabel}  ·  {result.Subtitle}";
                WebSummaryUrlText.Text = result.Body;
                WebSummaryDetailText.Text = result.Detail ?? string.Empty;
                WebSummaryDetailPanel.Visibility = string.IsNullOrWhiteSpace(result.Detail)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                WebSummaryPanel.Visibility = Visibility.Visible;
                break;
            case QuickLookRenderKind.Image:
                PreviewImage.Source = result.PreviewImage;
                PreviewImage.Visibility = Visibility.Visible;
                break;
            case QuickLookRenderKind.Text:
                PreviewText.Text = result.Body;
                PreviewText.Visibility = Visibility.Visible;
                TextOptionsPanel.Visibility = Visibility.Visible;
                ApplyTextDisplaySettings();
                break;
            case QuickLookRenderKind.Information:
                InformationIcon.Source = result.Icon;
                InformationIcon.Visibility = result.Icon is null ? Visibility.Collapsed : Visibility.Visible;
                InformationText.Text = result.Body;
                InformationPanel.Visibility = Visibility.Visible;
                break;
            case QuickLookRenderKind.Interactive:
                InteractiveContent.Content = result.InteractiveContent;
                InteractiveContent.Visibility = Visibility.Visible;
                break;
            default:
                StateText.Text = result.Body;
                StateText.Foreground = System.Windows.Media.Brushes.IndianRed;
                StateText.Visibility = Visibility.Visible;
                break;
        }

        UpdateButtons();
    }

    private void ShowOperationError(string message)
    {
        ClearContent();
        StateText.Text = message;
        StateText.Foreground = System.Windows.Media.Brushes.IndianRed;
        StateText.Visibility = Visibility.Visible;
    }

    private void ClearContent()
    {
        StateText.Visibility = Visibility.Collapsed;
        StateText.Foreground = System.Windows.Media.Brushes.LightGray;
        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewImage.Source = null;
        PreviewText.Visibility = Visibility.Collapsed;
        PreviewText.Clear();
        TextOptionsPanel.Visibility = Visibility.Collapsed;
        InformationPanel.Visibility = Visibility.Collapsed;
        InformationIcon.Source = null;
        InformationText.Text = string.Empty;
        WebSummaryPanel.Visibility = Visibility.Collapsed;
        WebSummaryImage.Source = null;
        WebSummaryImageFallbackPanel.Visibility = Visibility.Visible;
        WebSummaryFavicon.Source = null;
        WebSummaryFavicon.Visibility = Visibility.Collapsed;
        WebSummaryFaviconFallbackText.Visibility = Visibility.Visible;
        WebSummaryHeroFavicon.Source = null;
        WebSummaryHeroFavicon.Visibility = Visibility.Collapsed;
        WebSummaryHeroFaviconFallbackText.Visibility = Visibility.Visible;
        WebSummaryTitleText.Text = string.Empty;
        WebSummaryDomainText.Text = string.Empty;
        WebSummaryUrlText.Text = string.Empty;
        WebSummaryDetailPanel.Visibility = Visibility.Collapsed;
        WebSummaryDetailText.Text = string.Empty;
        InteractiveContent.Visibility = Visibility.Collapsed;
        InteractiveContent.Content = null;
        LimitText.Visibility = Visibility.Collapsed;
    }

    private void UpdateButtons()
    {
        var source = _request?.ItemSource;
        PreviousButton.IsEnabled = source?.CanMovePrevious == true;
        NextButton.IsEnabled = source?.CanMoveNext == true;
        var singleItem = _request?.Items.Count == 1 ? _request.Items[0] : null;
        var primaryAction = singleItem?.Actions.FirstOrDefault(static action => action.IsPrimary);
        AdditionalActionsList.ItemsSource = singleItem?.Actions
            .Where(static action => !action.IsPrimary)
            .ToList() ?? [];
        OpenButton.Content = primaryAction?.Label ?? "開く";
        OpenButton.IsEnabled = singleItem is not null
            && (primaryAction is not null || singleItem.FilePath is not null || singleItem.Uri is not null);
        OpenButton.Visibility = OpenButton.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        var canShowInFolder = singleItem?.FilePath is not null;
        ShowInFolderButton.IsEnabled = canShowInFolder;
        ShowInFolderButton.Visibility = canShowInFolder ? Visibility.Visible : Visibility.Collapsed;
        CopyButton.Visibility = singleItem is not null && GetCopyText(singleItem) is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static bool IsExpectedShellOperationException(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or NotSupportedException
            or Win32Exception
            or SecurityException;
    }

    private void ApplyDisplayMode()
    {
        RightResizeThumb.Visibility = Visibility.Collapsed;
        BottomResizeThumb.Visibility = Visibility.Collapsed;
        FloatingResizeThumb.Visibility = Visibility.Collapsed;
        PreviewPanel.RenderTransform = null;
        UpdateDisplayModeButtons();
        if (_useRightSidePane)
        {
            PreviewPanel.Margin = new Thickness(0);
            PreviewPanel.CornerRadius = new CornerRadius(0);
            PaneDragHeader.Cursor = Cursors.SizeAll;
            PaneDragHeader.ToolTip = "右端または下端へドラッグしてペインを移動";
            if (string.Equals(_panePlacement, QuickLookPanePlacement.Bottom, StringComparison.Ordinal))
            {
                PreviewPanel.Width = double.NaN;
                PreviewPanel.Height = ConstrainToAvailable(_bottomPaneHeight, ActualHeight);
                PreviewPanel.MaxWidth = double.PositiveInfinity;
                PreviewPanel.MaxHeight = double.PositiveInfinity;
                PreviewPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
                PreviewPanel.VerticalAlignment = VerticalAlignment.Bottom;
                BottomResizeThumb.Visibility = Visibility.Visible;
                ApplyDockTargetLayout();
                return;
            }

            PreviewPanel.Width = ConstrainToAvailable(_rightPaneWidth, ActualWidth);
            PreviewPanel.Height = double.NaN;
            PreviewPanel.MaxWidth = double.PositiveInfinity;
            PreviewPanel.MaxHeight = double.PositiveInfinity;
            PreviewPanel.HorizontalAlignment = HorizontalAlignment.Right;
            PreviewPanel.VerticalAlignment = VerticalAlignment.Stretch;
            RightResizeThumb.Visibility = Visibility.Visible;
            ApplyDockTargetLayout();
            return;
        }

        PreviewPanel.MaxWidth = double.PositiveInfinity;
        PreviewPanel.MaxHeight = double.PositiveInfinity;
        PreviewPanel.Margin = new Thickness(
            FloatingHorizontalOuterMargin,
            FloatingVerticalOuterMargin,
            FloatingHorizontalOuterMargin,
            FloatingVerticalOuterMargin);
        PreviewPanel.HorizontalAlignment = HorizontalAlignment.Center;
        PreviewPanel.VerticalAlignment = VerticalAlignment.Center;
        PreviewPanel.CornerRadius = new CornerRadius(4);
        PaneDragHeader.Cursor = Cursors.SizeAll;
        PaneDragHeader.ToolTip = "ドラッグしてQuickLookを移動";
        FloatingResizeThumb.Visibility = Visibility.Visible;
        ApplyFloatingSize();
        ApplyDockTargetLayout();
    }

    private void UpdatePaneSize()
    {
        if (!_useRightSidePane)
        {
            return;
        }

        if (string.Equals(_panePlacement, QuickLookPanePlacement.Bottom, StringComparison.Ordinal))
        {
            PreviewPanel.Height = ConstrainToAvailable(_bottomPaneHeight, ActualHeight);
            ApplyDockTargetLayout();
            return;
        }

        PreviewPanel.Width = ConstrainToAvailable(_rightPaneWidth, ActualWidth);
        ApplyDockTargetLayout();
    }

    private void UpdateLayoutForCurrentMode()
    {
        if (_useRightSidePane)
        {
            UpdatePaneSize();
        }
        else
        {
            ApplyFloatingSize();
        }
    }

    private void ApplyFloatingSize()
    {
        if (_useRightSidePane)
        {
            return;
        }

        var width = ConstrainFloatingDimension(
            _floatingWidth,
            GetFloatingAvailableDimension(ActualWidth, FloatingHorizontalOuterMargin),
            QuickLookSettingsProfile.MinimumFloatingWidth,
            QuickLookSettingsProfile.MaximumFloatingWidth);
        var height = ConstrainFloatingDimension(
            _floatingHeight,
            GetFloatingAvailableDimension(ActualHeight, FloatingVerticalOuterMargin),
            QuickLookSettingsProfile.MinimumFloatingHeight,
            QuickLookSettingsProfile.MaximumFloatingHeight);
        PreviewPanel.Width = width;
        PreviewPanel.Height = height;
        ApplyFloatingPosition(width, height);
    }

    private void ApplyFloatingPosition(double? renderedWidth = null, double? renderedHeight = null)
    {
        if (_useRightSidePane)
        {
            return;
        }

        var panelWidth = renderedWidth ?? PreviewPanel.ActualWidth;
        var panelHeight = renderedHeight ?? PreviewPanel.ActualHeight;
        if (ActualWidth > 0 && ActualHeight > 0 && panelWidth > 0 && panelHeight > 0)
        {
            var baseLeft = (ActualWidth - panelWidth) / 2;
            var baseTop = (ActualHeight - panelHeight) / 2;
            _floatingOffsetX = ClampOffset(
                _floatingOffsetX,
                8 - baseLeft,
                ActualWidth - panelWidth - baseLeft - 8);
            _floatingOffsetY = ClampOffset(
                _floatingOffsetY,
                8 - baseTop,
                ActualHeight - panelHeight - baseTop - 8);
        }

        PreviewPanel.RenderTransform = new TranslateTransform(_floatingOffsetX, _floatingOffsetY);
    }

    private void ApplyDockTargetLayout()
    {
        if (_dockTarget is null)
        {
            return;
        }

        var margin = _dockTargetBaseMargin;
        if (IsOpen && _useRightSidePane)
        {
            if (string.Equals(_panePlacement, QuickLookPanePlacement.Bottom, StringComparison.Ordinal))
            {
                margin.Bottom += ConstrainToAvailable(_bottomPaneHeight, ActualHeight);
            }
            else
            {
                margin.Right += ConstrainToAvailable(_rightPaneWidth, ActualWidth);
            }
        }

        _dockTarget.Margin = margin;
    }

    private void RestoreDockTargetMargin()
    {
        if (_dockTarget is not null)
        {
            _dockTarget.Margin = _dockTargetBaseMargin;
        }
    }

    private static double ClampOffset(double value, double minimum, double maximum)
    {
        return maximum >= minimum ? Math.Clamp(value, minimum, maximum) : 0;
    }

    private bool IsOwnPaneDrag(IDataObject data)
    {
        return data.GetDataPresent(PaneDragDataFormat)
            && ReferenceEquals(data.GetData(PaneDragDataFormat), this);
    }

    private string ResolvePanePlacement(Point point)
    {
        var width = Math.Max(1, HostRoot.ActualWidth);
        var height = Math.Max(1, HostRoot.ActualHeight);
        var distanceFromRight = Math.Max(0, width - point.X) / width;
        var distanceFromBottom = Math.Max(0, height - point.Y) / height;
        return distanceFromBottom <= distanceFromRight
            ? QuickLookPanePlacement.Bottom
            : QuickLookPanePlacement.Right;
    }

    private static double ConstrainToAvailable(double desired, double available)
    {
        return available > 0 ? Math.Min(desired, available) : desired;
    }

    private static double GetFloatingAvailableDimension(double total, double outerMargin)
    {
        return total > 0
            ? Math.Max(1, total - (outerMargin * 2))
            : double.NaN;
    }

    private static double ConstrainFloatingDimension(
        double desired,
        double available,
        double minimum,
        double maximum)
    {
        var maximumAvailable = available > 0
            ? Math.Min(maximum, available)
            : maximum;
        var minimumAvailable = Math.Min(minimum, maximumAvailable);
        return Math.Clamp(desired, minimumAvailable, maximumAvailable);
    }

    private void NotifyPaneLayoutChanged()
    {
        PaneLayoutChanged?.Invoke(
            this,
            new QuickLookPaneLayoutChangedEventArgs(
                _useRightSidePane,
                _panePlacement,
                _rightPaneWidth,
                _bottomPaneHeight,
                _floatingOffsetX,
                _floatingOffsetY,
                _floatingWidth,
                _floatingHeight));
    }

    private void SetDisplayMode(bool usePane, string panePlacement)
    {
        var normalizedPlacement = QuickLookPanePlacement.Normalize(panePlacement);
        if (_useRightSidePane == usePane
            && (!usePane || string.Equals(_panePlacement, normalizedPlacement, StringComparison.Ordinal)))
        {
            return;
        }

        _useRightSidePane = usePane;
        if (usePane)
        {
            _panePlacement = normalizedPlacement;
        }

        ApplyDisplayMode();
        NotifyPaneLayoutChanged();
    }

    private void SetTextFontSize(int fontSize)
    {
        var normalized = Math.Clamp(
            fontSize,
            QuickLookSettingsProfile.MinimumTextFontSize,
            QuickLookSettingsProfile.MaximumTextFontSize);
        if (_textFontSize == normalized)
        {
            return;
        }

        _textFontSize = normalized;
        ApplyTextDisplaySettings();
        TextDisplayChanged?.Invoke(this, new QuickLookTextDisplayChangedEventArgs(_textFontSize, _textWrap));
    }

    private void ApplyTextDisplaySettings()
    {
        PreviewText.FontSize = _textFontSize;
        TextBlock.SetLineHeight(PreviewText, Math.Ceiling(_textFontSize * 1.5));
        PreviewText.TextWrapping = _textWrap ? TextWrapping.WrapWithOverflow : TextWrapping.NoWrap;
        PreviewText.HorizontalScrollBarVisibility = _textWrap
            ? ScrollBarVisibility.Disabled
            : ScrollBarVisibility.Auto;
        TextFontSizeText.Text = $"{_textFontSize}px";
        TextSmallerButton.IsEnabled = _textFontSize > QuickLookSettingsProfile.MinimumTextFontSize;
        TextLargerButton.IsEnabled = _textFontSize < QuickLookSettingsProfile.MaximumTextFontSize;
        SetControlSelected(TextWrapButton, _textWrap);
    }

    private void UpdateDisplayModeButtons()
    {
        SetControlSelected(FloatingModeButton, !_useRightSidePane);
        SetControlSelected(
            RightPaneModeButton,
            _useRightSidePane
                && string.Equals(_panePlacement, QuickLookPanePlacement.Right, StringComparison.Ordinal));
        SetControlSelected(
            BottomPaneModeButton,
            _useRightSidePane
                && string.Equals(_panePlacement, QuickLookPanePlacement.Bottom, StringComparison.Ordinal));
    }

    private static void SetControlSelected(Button button, bool isSelected)
    {
        button.Background = isSelected ? SelectedControlBackground : NormalControlBackground;
        button.BorderBrush = isSelected ? SelectedControlBorder : NormalControlBorder;
        button.FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal;
    }
}

public sealed class QuickLookPaneLayoutChangedEventArgs(
    bool useRightSidePane,
    string panePlacement,
    double rightPaneWidth,
    double bottomPaneHeight,
    double floatingOffsetX,
    double floatingOffsetY,
    double floatingWidth,
    double floatingHeight) : EventArgs
{
    public bool UseRightSidePane { get; } = useRightSidePane;

    public string PanePlacement { get; } = QuickLookPanePlacement.Normalize(panePlacement);

    public double RightPaneWidth { get; } = rightPaneWidth;

    public double BottomPaneHeight { get; } = bottomPaneHeight;

    public double FloatingOffsetX { get; } = floatingOffsetX;

    public double FloatingOffsetY { get; } = floatingOffsetY;

    public double FloatingWidth { get; } = floatingWidth;

    public double FloatingHeight { get; } = floatingHeight;
}

public sealed class QuickLookTextDisplayChangedEventArgs(int textFontSize, bool textWrap) : EventArgs
{
    public int TextFontSize { get; } = Math.Clamp(
        textFontSize,
        QuickLookSettingsProfile.MinimumTextFontSize,
        QuickLookSettingsProfile.MaximumTextFontSize);

    public bool TextWrap { get; } = textWrap;
}
