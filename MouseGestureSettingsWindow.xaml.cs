using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using TabBrowser.Models;

namespace TabBrowser;

public partial class MouseGestureSettingsWindow : Window
{
    private const string AllScopeFilter = "__all";
    private const double DirectionDominanceRatio = 1.25;
    private const double ReverseDirectionThresholdMultiplier = 1.5;
    private const double MinimumStartThreshold = 4;
    private const double MaximumStartThreshold = 80;
    private const double MinimumDirectionThreshold = 8;
    private const double MaximumDirectionThreshold = 120;
    private readonly ICollectionView _mappingView;
    private readonly ICollectionView _quickCommandView;
    private readonly IReadOnlyList<GestureCommandOption> _commandOptions;
    private string? _pendingTargetCommand;
    private bool _isGestureRecording;
    private bool _isGestureRecorderArmed;
    private string _recordedRoute = string.Empty;
    private Point? _gestureStartPoint;
    private Point? _gestureLastDirectionPoint;

    public MouseGestureSettingsWindow(
        AppSettings settings,
        string? targetCommand = null,
        IReadOnlyList<GestureCommandOption>? commandOptions = null)
    {
        InitializeComponent();

        _commandOptions = commandOptions is { Count: > 0 }
            ? commandOptions
            : GestureSettingsCatalog.CommandOptions;
        GestureMappings = new ObservableCollection<GestureMappingSettings>(
            GestureSettingsCatalog.NormalizeMappings(settings.GestureMappings).Select(CloneMapping));
        StartThresholdBox.Text = settings.GestureStartThreshold.ToString(CultureInfo.CurrentCulture);
        DirectionThresholdBox.Text = settings.GestureDirectionThreshold.ToString(CultureInfo.CurrentCulture);
        PreserveContextMenuCheckBox.IsChecked = settings.PreserveContextMenuOnShortRightClick;
        _quickCommandView = new ListCollectionView(_commandOptions.ToList())
        {
            Filter = MatchesQuickCommand
        };
        DataContext = this;
        _mappingView = CollectionViewSource.GetDefaultView(GestureMappings);
        _mappingView.Filter = MatchesMappingSearch;
        QuickScopeBox.SelectedValue = GestureCommandScope.Any;
        MappingScopeFilterBox.SelectedValue = AllScopeFilter;
        _pendingTargetCommand = targetCommand;
        Loaded += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_pendingTargetCommand))
            {
                SelectFirstVisibleQuickCommand();
                UpdateQuickAssignmentState();
            }
            else
            {
                FocusCommand(_pendingTargetCommand);
            }

            _pendingTargetCommand = null;
        };
    }

    public ObservableCollection<GestureMappingSettings> GestureMappings { get; }

    public IReadOnlyList<string> RouteOptions => GestureSettingsCatalog.CommonRoutes;

    public IReadOnlyList<GestureScopeOption> ScopeOptions => GestureCommandScope.Options;

    public IReadOnlyList<GestureScopeOption> MappingScopeFilterOptions { get; } =
    [
        new(AllScopeFilter, "すべて表示"),
        .. GestureCommandScope.Options
    ];

    public IReadOnlyList<GestureCommandOption> CommandOptions => _commandOptions;

    public ICollectionView QuickCommandOptions => _quickCommandView;

    public AppSettings ResultSettings { get; private set; } = new();

    public bool IsAccepted { get; private set; }

    public void FocusCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)
            || GetCommandOption(command) is not { } commandOption)
        {
            return;
        }

        MappingSearchBox.Text = string.Empty;
        var mapping = GestureMappings.FirstOrDefault(candidate =>
            candidate.Command.Equals(command, StringComparison.Ordinal));
        if (mapping is null)
        {
            var scope = commandOption.Scopes.FirstOrDefault() ?? GestureCommandScope.Any;
            var usedRoutes = GestureMappings
                .Where(candidate => GestureCommandScope.Normalize(candidate.Scope).Equals(
                    scope,
                    StringComparison.Ordinal))
                .Select(static candidate => candidate.Route)
                .ToHashSet(StringComparer.Ordinal);
            var route = RouteOptions.FirstOrDefault(candidate => !usedRoutes.Contains(candidate)) ?? string.Empty;
            mapping = new GestureMappingSettings
            {
                Route = route,
                Command = command,
                Label = commandOption.Label,
                Scope = scope
            };
            GestureMappings.Add(mapping);
            _mappingView.Refresh();
        }

        QuickScopeBox.SelectedValue = mapping.Scope;
        QuickCommandSearchBox.Text = string.Empty;
        _quickCommandView.Refresh();
        QuickCommandList.SelectedItem = commandOption;
        SetRecordedRoute(mapping.Route, "現在の割り当てを読み込みました。");
        MappingsGrid.SelectedItem = mapping;
        MappingsGrid.ScrollIntoView(mapping);
        MappingsGrid.Focus();
        ValidateDuplicateRoutesPreview();
    }

    private void AddMappingButton_Click(object sender, RoutedEventArgs e)
    {
        var usedRoutes = GestureMappings
            .Where(static mapping => mapping.Scope == GestureCommandScope.Any)
            .Select(static x => x.Route)
            .ToHashSet(StringComparer.Ordinal);
        var route = RouteOptions.FirstOrDefault(x => !usedRoutes.Contains(x)) ?? "←";
        var mapping = new GestureMappingSettings
        {
            Route = route,
            Command = "Back",
            Label = GetCommandLabel("Back"),
            Scope = GestureCommandScope.Any
        };

        GestureMappings.Add(mapping);
        _mappingView.Refresh();
        MappingsGrid.SelectedItem = mapping;
        MappingsGrid.ScrollIntoView(mapping);
        SetError(string.Empty);
    }

    private void ResetDefaultsButton_Click(object sender, RoutedEventArgs e)
    {
        GestureMappings.Clear();
        foreach (var mapping in GestureSettingsCatalog.CreateDefaultMappings())
        {
            GestureMappings.Add(mapping);
        }

        _quickCommandView.Refresh();
        SelectFirstVisibleQuickCommand();
        _mappingView.Refresh();
        SetError(string.Empty);
    }

    private void RemoveMappingButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is GestureMappingSettings mapping)
        {
            GestureMappings.Remove(mapping);
            _mappingView.Refresh();
            SetError(string.Empty);
        }
    }

    private void MappingSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _mappingView.Refresh();
    }

    private void MappingScopeFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _mappingView?.Refresh();
    }

    private bool MatchesMappingSearch(object item)
    {
        if (item is not GestureMappingSettings mapping)
        {
            return false;
        }

        var selectedScope = MappingScopeFilterBox.SelectedValue as string ?? AllScopeFilter;
        var mappingScope = GestureCommandScope.Normalize(mapping.Scope);
        if (!selectedScope.Equals(AllScopeFilter, StringComparison.Ordinal))
        {
            var normalizedSelectedScope = GestureCommandScope.Normalize(selectedScope);
            var isInheritedMapping = !normalizedSelectedScope.Equals(
                    GestureCommandScope.Any,
                    StringComparison.Ordinal)
                && !normalizedSelectedScope.Equals(
                    GestureCommandScope.MiniPlayer,
                    StringComparison.Ordinal)
                && mappingScope.Equals(GestureCommandScope.Any, StringComparison.Ordinal);
            if (!mappingScope.Equals(normalizedSelectedScope, StringComparison.Ordinal)
                && !isInheritedMapping)
            {
                return false;
            }
        }

        var query = MappingSearchBox.Text.Trim();
        return query.Length == 0
            || mapping.Route.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || mapping.Command.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || GetCommandLabel(mapping.Command).Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || GestureCommandScope.GetLabel(mapping.Scope)
                .Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || GetCommandOption(mapping.Command)?.Category
                .Contains(query, StringComparison.CurrentCultureIgnoreCase) == true
            || GetCommandOption(mapping.Command)?.Keywords
                .Any(keyword => keyword.Contains(query, StringComparison.CurrentCultureIgnoreCase)) == true;
    }

    private void QuickScopeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _quickCommandView?.Refresh();
        if (QuickCommandList.SelectedItem is not GestureCommandOption selected
            || !selected.SupportsScope(GetQuickScope()))
        {
            SelectFirstVisibleQuickCommand();
        }

        UpdateQuickAssignmentState();
    }

    private void QuickCommandSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _quickCommandView?.Refresh();
        SelectFirstVisibleQuickCommand();
        UpdateQuickAssignmentState();
    }

    private bool MatchesQuickCommand(object item)
    {
        if (item is not GestureCommandOption option || !option.SupportsScope(GetQuickScope()))
        {
            return false;
        }

        var query = QuickCommandSearchBox.Text.Trim();
        return query.Length == 0
            || option.Label.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || option.Command.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || option.Category.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || option.Keywords.Any(keyword =>
                keyword.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }

    private void QuickCommandList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateQuickAssignmentState();
    }

    private void RecordGestureButton_Click(object sender, RoutedEventArgs e)
    {
        PrepareGestureRecorder();
        GestureRecorderSurface.Focus();
    }

    private void PrepareGestureRecorder()
    {
        CancelGestureRecording(clearRoute: true);
        _isGestureRecorderArmed = true;
        RecordGestureButton.Content = "記録待機中";
        GestureRecorderHintText.Text = "右ボタンを押したまま描く";
        GestureRecorderHintText.Visibility = Visibility.Visible;
        RecorderStatusText.Text =
            $"異なる方向を最大{GestureSettingsCatalog.MaximumDirectionCount}つまで記録します。";
    }

    private void GestureRecorderSurface_PreviewMouseRightButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        CancelGestureRecording(clearRoute: true);
        _isGestureRecorderArmed = true;
        _isGestureRecording = true;
        _gestureStartPoint = e.GetPosition(GestureRecorderCanvas);
        _gestureLastDirectionPoint = _gestureStartPoint;
        GestureRecorderPath.Points.Clear();
        GestureRecorderPath.Points.Add(_gestureStartPoint.Value);
        GestureRecorderHintText.Visibility = Visibility.Collapsed;
        RecordGestureButton.Content = "記録中";
        RecorderStatusText.Text = "右ボタンを離すと入力を確定します。";
        GestureRecorderSurface.CaptureMouse();
        e.Handled = true;
    }

    private void GestureRecorderSurface_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isGestureRecording
            || e.RightButton != MouseButtonState.Pressed
            || _gestureStartPoint is not { } startPoint
            || _gestureLastDirectionPoint is not { } lastDirectionPoint)
        {
            return;
        }

        var point = e.GetPosition(GestureRecorderCanvas);
        GestureRecorderPath.Points.Add(point);
        if (GetDistance(startPoint, point) >= ReadRecorderThreshold(
                StartThresholdBox,
                MinimumStartThreshold,
                MaximumStartThreshold,
                fallback: 14))
        {
            AppendRecordedDirection(lastDirectionPoint, point);
        }

        e.Handled = true;
    }

    private void AppendRecordedDirection(Point from, Point to)
    {
        var deltaX = to.X - from.X;
        var deltaY = to.Y - from.Y;
        var absoluteX = Math.Abs(deltaX);
        var absoluteY = Math.Abs(deltaY);
        var directionThreshold = ReadRecorderThreshold(
            DirectionThresholdBox,
            MinimumDirectionThreshold,
            MaximumDirectionThreshold,
            fallback: 22);
        if (Math.Max(absoluteX, absoluteY) < directionThreshold)
        {
            return;
        }

        var horizontal = absoluteX >= absoluteY;
        var primaryDistance = horizontal ? absoluteX : absoluteY;
        var secondaryDistance = horizontal ? absoluteY : absoluteX;
        if (primaryDistance < secondaryDistance * DirectionDominanceRatio)
        {
            return;
        }

        var direction = horizontal
            ? deltaX < 0 ? "←" : "→"
            : deltaY < 0 ? "↑" : "↓";
        var previousDirection = _recordedRoute.Length > 0
            ? _recordedRoute[^1].ToString()
            : string.Empty;
        if (previousDirection.Length > 0
            && IsOppositeDirection(previousDirection, direction)
            && primaryDistance < directionThreshold * ReverseDirectionThresholdMultiplier)
        {
            return;
        }

        if (!previousDirection.Equals(direction, StringComparison.Ordinal)
            && _recordedRoute.Length < GestureSettingsCatalog.MaximumDirectionCount)
        {
            _recordedRoute += direction;
            RecordedRouteText.Text = _recordedRoute;
        }

        _gestureLastDirectionPoint = to;
        UpdateQuickAssignmentState();
    }

    private void GestureRecorderSurface_PreviewMouseRightButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (!_isGestureRecording)
        {
            return;
        }

        CompleteGestureRecording();
        e.Handled = true;
    }

    private void GestureRecorderSurface_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_isGestureRecording)
        {
            CancelGestureRecording(clearRoute: true);
        }
    }

    private void CompleteGestureRecording()
    {
        _isGestureRecording = false;
        _isGestureRecorderArmed = false;
        _gestureStartPoint = null;
        _gestureLastDirectionPoint = null;
        if (GestureRecorderSurface.IsMouseCaptured)
        {
            GestureRecorderSurface.ReleaseMouseCapture();
        }

        RecordGestureButton.Content = "入力を記録";
        if (GestureSettingsCatalog.IsSupportedRoute(_recordedRoute))
        {
            GestureRecorderHintText.Visibility = Visibility.Collapsed;
            RecorderStatusText.Text = $"入力「{_recordedRoute}」を記録しました。";
        }
        else
        {
            _recordedRoute = string.Empty;
            RecordedRouteText.Text = "未記録";
            GestureRecorderHintText.Text = "ここを右ドラッグ";
            GestureRecorderHintText.Visibility = Visibility.Visible;
            RecorderStatusText.Text = "方向を認識できませんでした。もう一度右ドラッグしてください。";
        }

        UpdateQuickAssignmentState();
    }

    private void CancelGestureRecording(bool clearRoute)
    {
        _isGestureRecording = false;
        _isGestureRecorderArmed = false;
        _gestureStartPoint = null;
        _gestureLastDirectionPoint = null;
        if (GestureRecorderSurface.IsMouseCaptured)
        {
            GestureRecorderSurface.ReleaseMouseCapture();
        }

        RecordGestureButton.Content = "入力を記録";
        if (clearRoute)
        {
            _recordedRoute = string.Empty;
            RecordedRouteText.Text = "未記録";
            GestureRecorderPath.Points.Clear();
            GestureRecorderHintText.Text = "ここを右ドラッグ";
            GestureRecorderHintText.Visibility = Visibility.Visible;
        }

        UpdateQuickAssignmentState();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || (!_isGestureRecording && !_isGestureRecorderArmed))
        {
            return;
        }

        CancelGestureRecording(clearRoute: true);
        RecorderStatusText.Text = "入力記録をキャンセルしました。";
        e.Handled = true;
    }

    private void QuickAssignButton_Click(object sender, RoutedEventArgs e)
    {
        if (!GestureSettingsCatalog.IsSupportedRoute(_recordedRoute)
            || QuickCommandList.SelectedItem is not GestureCommandOption commandOption)
        {
            return;
        }

        var scope = GetQuickScope();
        if (!commandOption.SupportsScope(scope))
        {
            SetError(
                $"{commandOption.Label} は {GestureCommandScope.GetLabel(scope)} では使用できません。");
            return;
        }

        var existing = FindQuickAssignment(scope, _recordedRoute);
        if (existing is null)
        {
            existing = new GestureMappingSettings
            {
                Route = _recordedRoute,
                Command = commandOption.Command,
                Label = commandOption.Label,
                Scope = scope
            };
            GestureMappings.Add(existing);
            QuickAssignmentStatusText.Text =
                $"{GestureCommandScope.GetLabel(scope)} / {_recordedRoute} に割り当てました。";
        }
        else
        {
            existing.Command = commandOption.Command;
            existing.Label = commandOption.Label;
            QuickAssignmentStatusText.Text =
                $"{GestureCommandScope.GetLabel(scope)} / {_recordedRoute} の機能を置き換えました。";
        }

        _mappingView.Refresh();
        MappingsGrid.SelectedItem = existing;
        MappingsGrid.ScrollIntoView(existing);
        SetError(string.Empty);
        UpdateQuickAssignmentState(preserveStatus: true);
    }

    private void UpdateQuickAssignmentState(bool preserveStatus = false)
    {
        if (QuickAssignButton is null || QuickAssignmentStatusText is null)
        {
            return;
        }

        var commandOption = QuickCommandList.SelectedItem as GestureCommandOption;
        var hasRoute = GestureSettingsCatalog.IsSupportedRoute(_recordedRoute);
        var scope = GetQuickScope();
        var canAssign = hasRoute
            && commandOption is not null
            && commandOption.SupportsScope(scope);
        QuickAssignButton.IsEnabled = canAssign;

        if (!hasRoute)
        {
            QuickAssignButton.Content = "割り当て";
            if (!preserveStatus)
            {
                QuickAssignmentStatusText.Text = "先に入力を記録してください。";
            }

            return;
        }

        var existing = FindQuickAssignment(scope, _recordedRoute);
        QuickAssignButton.Content = existing is null ? "割り当て" : "置き換え";
        if (preserveStatus)
        {
            return;
        }

        QuickAssignmentStatusText.Text = existing is null
            ? "この入力は未割り当てです。"
            : $"現在: {GetCommandLabel(existing.Command)}";
    }

    private GestureMappingSettings? FindQuickAssignment(string scope, string route)
    {
        return GestureMappings.FirstOrDefault(mapping =>
            GestureCommandScope.Normalize(mapping.Scope).Equals(scope, StringComparison.Ordinal)
            && (mapping.Route ?? string.Empty).Trim().Equals(route, StringComparison.Ordinal));
    }

    private void SelectFirstVisibleQuickCommand()
    {
        if (QuickCommandList.Items.Count == 0)
        {
            QuickCommandList.SelectedItem = null;
            return;
        }

        if (QuickCommandList.SelectedItem is not GestureCommandOption selected
            || !_quickCommandView.Contains(selected))
        {
            QuickCommandList.SelectedIndex = 0;
        }
    }

    private void SetRecordedRoute(string? route, string status)
    {
        _recordedRoute = (route ?? string.Empty).Trim();
        RecordedRouteText.Text = _recordedRoute.Length == 0 ? "未記録" : _recordedRoute;
        GestureRecorderPath.Points.Clear();
        GestureRecorderHintText.Visibility = _recordedRoute.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        RecorderStatusText.Text = status;
        UpdateQuickAssignmentState();
    }

    private string GetQuickScope()
    {
        return GestureCommandScope.Normalize(QuickScopeBox.SelectedValue as string);
    }

    private static double ReadRecorderThreshold(
        TextBox textBox,
        double minimum,
        double maximum,
        double fallback)
    {
        var text = textBox.Text.Trim();
        if ((double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value)
                || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            && value >= minimum
            && value <= maximum)
        {
            return value;
        }

        return fallback;
    }

    private static double GetDistance(Point first, Point second)
    {
        var deltaX = second.X - first.X;
        var deltaY = second.Y - first.Y;
        return Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
    }

    private static bool IsOppositeDirection(string first, string second)
    {
        return first is "←" && second is "→"
            || first is "→" && second is "←"
            || first is "↑" && second is "↓"
            || first is "↓" && second is "↑";
    }

    private void MappingsGrid_CurrentCellChanged(object? sender, EventArgs e)
    {
        _ = Dispatcher.BeginInvoke(new Action(ValidateDuplicateRoutesPreview));
    }

    private void ValidateDuplicateRoutesPreview()
    {
        var duplicate = GestureMappings
            .Select(static mapping => new
            {
                Route = mapping.Route?.Trim() ?? string.Empty,
                Scope = GestureCommandScope.Normalize(mapping.Scope)
            })
            .Where(static mapping => mapping.Route.Length > 0)
            .GroupBy(
                static mapping => mapping.Scope + "\0" + mapping.Route,
                StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is null)
        {
            SetError(string.Empty);
            return;
        }

        var first = duplicate.First();
        SetError(
            $"入力が重複しています: {GestureCommandScope.GetLabel(first.Scope)} / {first.Route}");
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        MappingsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        MappingsGrid.CommitEdit(DataGridEditingUnit.Row, true);

        if (!TryReadThreshold(
                StartThresholdBox,
                "開始しきい値",
                MinimumStartThreshold,
                MaximumStartThreshold,
                out var startThreshold)
            || !TryReadThreshold(
                DirectionThresholdBox,
                "方向しきい値",
                MinimumDirectionThreshold,
                MaximumDirectionThreshold,
                out var directionThreshold)
            || !TryBuildMappings(out var mappings))
        {
            return;
        }

        ResultSettings = new AppSettings
        {
            GestureStartThreshold = startThreshold,
            GestureDirectionThreshold = directionThreshold,
            PreserveContextMenuOnShortRightClick = PreserveContextMenuCheckBox.IsChecked == true,
            GestureMappings = mappings
        };
        IsAccepted = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        IsAccepted = false;
        Close();
    }

    private bool TryReadThreshold(
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
            SetError($"{label} は {minimum} から {maximum} の数値で指定してください。");
            return false;
        }

        return true;
    }

    private bool TryBuildMappings(out List<GestureMappingSettings> mappings)
    {
        mappings = [];
        var commandSet = CommandOptions
            .Select(static x => x.Command)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seenRoutes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var mapping in GestureMappings)
        {
            var route = (mapping.Route ?? string.Empty).Trim();
            var command = (mapping.Command ?? string.Empty).Trim();
            var scope = GestureCommandScope.Normalize(mapping.Scope);
            if (string.IsNullOrWhiteSpace(route))
            {
                SetError("入力が空の行があります。");
                return false;
            }

            if (!IsValidRoute(route))
            {
                SetError(
                    $"入力は異なる矢印を最大{GestureSettingsCatalog.MaximumDirectionCount}方向まで、"
                    + "または wheelUp / wheelDown にしてください。");
                return false;
            }

            if (scope.Equals(GestureCommandScope.MiniPlayer, StringComparison.Ordinal)
                && route.StartsWith("wheel", StringComparison.Ordinal))
            {
                SetError("ミニプレイヤーの右ボタン＋ホイールは動画ズーム専用です。");
                return false;
            }

            if (!commandSet.Contains(command))
            {
                SetError("動作が未設定の行があります。");
                return false;
            }

            var commandOption = GetCommandOption(command);
            if (commandOption is null || !commandOption.SupportsScope(scope))
            {
                SetError(
                    $"{GetCommandLabel(command)} は {GestureCommandScope.GetLabel(scope)} では使用できません。");
                return false;
            }

            var routeKey = scope + "\0" + route;
            if (!seenRoutes.Add(routeKey))
            {
                SetError(
                    $"入力が重複しています: {GestureCommandScope.GetLabel(scope)} / {route}");
                return false;
            }

            mappings.Add(new GestureMappingSettings
            {
                Route = route,
                Command = command,
                Label = commandOption.Label,
                Scope = scope
            });
        }

        SetError(string.Empty);
        return true;
    }

    private void SetError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private static bool IsValidRoute(string route)
    {
        return GestureSettingsCatalog.IsSupportedRoute(route);
    }

    private static GestureMappingSettings CloneMapping(GestureMappingSettings mapping)
    {
        return new GestureMappingSettings
        {
            Route = mapping.Route,
            Command = mapping.Command,
            Label = mapping.Label,
            Scope = GestureCommandScope.Normalize(mapping.Scope)
        };
    }

    private GestureCommandOption? GetCommandOption(string command)
    {
        return CommandOptions.FirstOrDefault(option =>
            option.Command.Equals(command, StringComparison.OrdinalIgnoreCase));
    }

    private string GetCommandLabel(string command)
    {
        return GetCommandOption(command)?.Label
            ?? GestureSettingsCatalog.GetLabel(command);
    }
}
