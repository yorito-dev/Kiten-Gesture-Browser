using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TabBrowser.Models;
using TabBrowser.Views;

namespace TabBrowser;

public partial class SuperDragSettingsWindow : Window
{
    private readonly SuperDragSettingsEditor _editor;
    private readonly SuperDragDirectionRecorder _directionRecorder = new();
    private bool _isRecording;
    private bool _isArrowInput;

    public SuperDragSettingsWindow(SuperDragSettings settings)
    {
        InitializeComponent();
        _editor = new(SuperDragEnabledCheckBox, SuperDragMappingsList, SuperDragExcludedHostsBox, settings);
        SuperDragSearchProvidersList.ItemsSource = _editor.SearchProviders;
        SearchProviderPresetBox.ItemsSource = SuperDragSettingsProfile.AddableSearchPresets;
        RouteKindBox.ItemsSource = new[] { new SuperDragActionOption("text", "テキスト"),
            new SuperDragActionOption("link", "リンク"), new SuperDragActionOption("image", "画像") };
        SuperDragSettingsEditor.RunWhenLoaded(this, () => SuperDragEnabledCheckBox.Focus());
    }

    public SuperDragSettings ResultSettings { get; private set; } = new();
    public bool IsAccepted { get; private set; }

    internal void BeginRouteRecording(Point point)
    {
        CancelRouteRecording();
        _directionRecorder.Begin(point);
        _isRecording = true;
        RouteRecorderHint.Visibility = Visibility.Collapsed;
        RouteRecorderPath.Points.Add(point);
        RouteStatusText.Text = "記録中。左ボタンを離すと方向を確定します。";
    }

    internal void ContinueRouteRecording(Point point)
    {
        if (!_isRecording) return;
        RouteRecorderPath.Points.Add(point);
        _directionRecorder.Move(point);
        UpdateRouteSelection(select: false);
    }

    internal void CompleteRouteRecording(Point point)
    {
        if (!_isRecording) return;
        ContinueRouteRecording(point);
        _isRecording = false;
        if (RouteRecorderSurface.IsMouseCaptured) RouteRecorderSurface.ReleaseMouseCapture();
        UpdateRouteSelection(select: true);
    }

    internal void CancelRouteRecording()
    {
        _isRecording = _isArrowInput = false;
        if (RouteRecorderSurface.IsMouseCaptured) RouteRecorderSurface.ReleaseMouseCapture();
        _directionRecorder.Reset();
        RouteRecorderPath.Points.Clear();
        RouteRecorderHint.Visibility = Visibility.Visible;
        SelectedRoutePanel.DataContext = null;
        SelectedRoutePanel.Visibility = Visibility.Collapsed;
        RouteStatusText.Text = "未指定。左ドラッグ、または矢印ボタンで指定してください。";
    }

    internal void AppendRouteDirection(string direction)
    {
        if (!_isArrowInput) CancelRouteRecording();
        _isArrowInput = true;
        _directionRecorder.AppendDirection(direction);
        UpdateRouteSelection(select: true);
    }

    private void UpdateRouteSelection(bool select)
    {
        RouteStatusText.Text = _directionRecorder.IsInvalid
            ? "対象外の経路です。上下左右1方向、または直角のL字2方向を指定してください。"
            : _directionRecorder.Route.Length == 0 ? "まだ方向を認識できません。もう少し長く動かしてください。"
            : "方向：" + _directionRecorder.Description;
        if (!select) return;
        var row = RouteKindBox.SelectedValue is string kind && !_directionRecorder.IsInvalid
            ? _editor.SelectMapping(kind, _directionRecorder.Route) : null;
        SelectedRoutePanel.DataContext = row;
        SelectedRoutePanel.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RouteRecorderSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        BeginRouteRecording(e.GetPosition(RouteRecorderSurface));
        RouteRecorderSurface.Focus();
        if (!RouteRecorderSurface.CaptureMouse()) CancelRouteRecording();
        e.Handled = true;
    }

    private void RouteRecorderSurface_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isRecording) return;
        if (e.LeftButton == MouseButtonState.Pressed) ContinueRouteRecording(e.GetPosition(RouteRecorderSurface));
        else CancelRouteRecording();
        e.Handled = true;
    }

    private void RouteRecorderSurface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isRecording) return;
        CompleteRouteRecording(e.GetPosition(RouteRecorderSurface));
        e.Handled = true;
    }

    private void RouteRecorderSurface_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_isRecording) CancelRouteRecording();
    }

    private void DirectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string direction }) AppendRouteDirection(direction);
    }

    private void ClearRouteButton_Click(object sender, RoutedEventArgs e) => CancelRouteRecording();

    private void RouteKindBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_editor is not null) CancelRouteRecording();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || (!_isRecording && !RouteRecorderSurface.IsKeyboardFocusWithin)) return;
        CancelRouteRecording();
        e.Handled = true;
    }

    private void Window_Deactivated(object? sender, EventArgs e) { if (_isRecording) CancelRouteRecording(); }
    private void Window_Closed(object? sender, EventArgs e) => CancelRouteRecording();

    private void RemoveRouteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SuperDragSettingsEditor.MappingRow row })
        {
            if (ReferenceEquals(SelectedRoutePanel.DataContext, row)) CancelRouteRecording();
            _editor.RemoveMapping(row);
        }
    }

    private void ResetSuperDragButton_Click(object sender, RoutedEventArgs e)
    {
        CancelRouteRecording();
        _editor.Load(new());
        ValidationErrorText.Visibility = Visibility.Collapsed;
    }

    private void AddSearchProviderButton_Click(object sender, RoutedEventArgs e) => _editor.AddSearchProvider();

    private void AddPresetSearchProviderButton_Click(object sender, RoutedEventArgs e)
    {
        if (SearchProviderPresetBox.SelectedItem is SuperDragSearchPreset preset) _editor.AddSearchProvider(preset);
    }

    private void RemoveSearchProviderButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SuperDragSettingsEditor.SearchProviderRow row }) _editor.RemoveSearchProvider(row);
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        CancelRouteRecording();
        if (!_editor.TryBuildResult(out var result, out var error))
        {
            ValidationErrorText.Text = error;
            ValidationErrorText.Visibility = Visibility.Visible;
            return;
        }
        ResultSettings = result;
        IsAccepted = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        IsAccepted = false;
        Close();
    }
}
