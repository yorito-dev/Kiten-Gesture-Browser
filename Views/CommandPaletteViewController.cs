using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TabBrowser.Models;
using TabBrowser.Models.QuickLook;
using TabBrowser.Services;
using TabBrowser.Services.QuickLook;

namespace TabBrowser.Views;

internal interface ICommandPaletteViewHost
{
    bool IsQuickLookEnabled { get; }
    List<CommandPaletteUsageSettings> Usage { get; set; }
    CommandPaletteItem? CreateSearchAliasItem(string? input);
    IReadOnlyList<CommandPaletteItem> Search(string? query, bool includeUnavailable);
    void QueueApplicationIconLoad();
    void QueueExecution(CommandPaletteExecutionMode mode, CommandPaletteItem item);
    void ClosePalette();
    void ApplyShortcutWindowOrder();
    void CloseContext();
    void ResetShortcutWindowOrder();
    void SetFocus(IntPtr handle);
    void SaveSettings();
}

internal sealed record CommandPaletteElements(
    Popup CommandPaletteOverlay,
    FrameworkElement CommandPaletteOverlaySurface,
    FrameworkElement CommandPalettePanel,
    TranslateTransform CommandPalettePanelTransform,
    TextBox CommandPaletteSearchBox,
    CheckBox CommandPaletteShowAllCheckBox,
    CheckBox CommandPaletteCommandsOnlyCheckBox,
    ListBox CommandPaletteList,
    TextBlock CommandPaletteNoResultsText,
    QuickLookHost CommandPaletteQuickLookHost);

// 検索・実行サービスとは別に、パレットの表示、入力、選択と更新予約を所有する。
internal sealed class CommandPaletteViewController : IDisposable
{
    internal const int CommandPaletteSearchRefreshDelayMilliseconds = 80;
    internal const int CommandPalettePageSelectionCount = 8;
    private const double CommandPalettePanelEdgeMargin = 8;
    private static readonly TimeSpan CommandPaletteUsageSaveDelay = TimeSpan.FromSeconds(30);
    private readonly CommandPaletteElements _elements;
    private readonly ICommandPaletteViewHost _host;
    private readonly string _quickLookSourceId;
    private readonly ObservableCollection<CommandPaletteItem> _commandPaletteItems = [];
    private readonly OwnedDispatcherTimer _commandPaletteSearchRefreshTimer;
    private readonly OwnedDispatcherTimer _commandPaletteUsageSaveTimer = new(
        DispatcherTimerOwner.CommandPalette,
        DispatcherTimerTerminationPhase.WindowClosed);
    private bool _isCommandPaletteImeCompositionActive;
    private bool _suppressCommandPaletteQuickLookSelectionRefresh;
    private bool _commandPaletteShowUnavailable;
    private bool _commandPaletteCommandsOnly;
    private string? _activeCommandPaletteSearchAliasItemId;
    private SearchQuickLookItemSource? _commandPaletteQuickLookSource;
    private bool _initialized;
    private bool _disposed;
    private DispatcherOperation? _commandPaletteFocusOperation;
    private DispatcherOperation? _commandPaletteTextInputOperation;
    private readonly Queue<string?> _pendingCommandPaletteTextInput = new();
    private int _commandPaletteInputGeneration;

    private Popup CommandPaletteOverlay => _elements.CommandPaletteOverlay;
    private FrameworkElement CommandPaletteOverlaySurface => _elements.CommandPaletteOverlaySurface;
    private FrameworkElement CommandPalettePanel => _elements.CommandPalettePanel;
    private TranslateTransform CommandPalettePanelTransform => _elements.CommandPalettePanelTransform;
    private TextBox CommandPaletteSearchBox => _elements.CommandPaletteSearchBox;
    private CheckBox CommandPaletteShowAllCheckBox => _elements.CommandPaletteShowAllCheckBox;
    private CheckBox CommandPaletteCommandsOnlyCheckBox => _elements.CommandPaletteCommandsOnlyCheckBox;
    private ListBox CommandPaletteList => _elements.CommandPaletteList;
    private TextBlock CommandPaletteNoResultsText => _elements.CommandPaletteNoResultsText;
    private QuickLookHost CommandPaletteQuickLookHost => _elements.CommandPaletteQuickLookHost;
    private Dispatcher Dispatcher => CommandPaletteSearchBox.Dispatcher;
    internal bool IsCommandPaletteOpen => !_disposed && CommandPaletteOverlay.IsOpen;
    internal IReadOnlyList<CommandPaletteItem> Items => _commandPaletteItems;

    internal CommandPaletteViewController(
        CommandPaletteElements elements,
        OwnedDispatcherTimer searchRefreshTimer,
        string quickLookSourceId,
        ICommandPaletteViewHost host)
    {
        _elements = elements;
        _commandPaletteSearchRefreshTimer = searchRefreshTimer;
        _quickLookSourceId = quickLookSourceId;
        _host = host;
    }

    internal void Initialize()
    {
        if (_initialized || _disposed) return;
        _initialized = true;
        _commandPaletteSearchRefreshTimer.Tick += SearchRefreshTimer_Tick;
        _commandPaletteUsageSaveTimer.Interval = CommandPaletteUsageSaveDelay;
        _commandPaletteUsageSaveTimer.Tick += UsageSaveTimer_Tick;
        CommandPaletteList.ItemsSource = _commandPaletteItems;
        _commandPaletteQuickLookSource = new SearchQuickLookItemSource(
            _quickLookSourceId,
            CreateCommandPaletteQuickLookItems,
            CreateSelectedCommandPaletteQuickLookItems,
            () => CommandPaletteList.SelectedIndex,
            SelectCommandPaletteQuickLookIndex);
        TextCompositionManager.AddPreviewTextInputStartHandler(CommandPaletteSearchBox, CommandPaletteSearchBox_PreviewTextInputStart);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(CommandPaletteSearchBox, CommandPaletteSearchBox_PreviewTextInputUpdate);
        TextCompositionManager.AddPreviewTextInputHandler(CommandPaletteSearchBox, CommandPaletteSearchBox_PreviewTextInput);
    }

    private void SearchRefreshTimer_Tick(object? sender, EventArgs e)
    {
        _commandPaletteSearchRefreshTimer.Stop();
        if (IsCommandPaletteOpen) RefreshCommandPaletteItems();
    }

    private void UsageSaveTimer_Tick(object? sender, EventArgs e)
    {
        _commandPaletteUsageSaveTimer.Stop();
        if (!_disposed) _host.SaveSettings();
    }

    internal void PrepareOpen()
    {
        if (_disposed) return;
        ResetCommandPaletteInput();
        _commandPaletteSearchRefreshTimer.Stop();
        _isCommandPaletteImeCompositionActive = false;
    }

    internal void Show()
    {
        if (_disposed) return;
        CommandPaletteSearchBox.Text = string.Empty;
        CommandPaletteShowAllCheckBox.IsChecked = _commandPaletteShowUnavailable;
        CommandPaletteCommandsOnlyCheckBox.IsChecked = _commandPaletteCommandsOnly;
        CommandPaletteOverlay.IsOpen = true;
        RefreshCommandPaletteItems();
    }

    internal void Close()
    {
        if (_disposed) return;
        ResetCommandPaletteInput();
        _commandPaletteSearchRefreshTimer.Stop();
        _isCommandPaletteImeCompositionActive = false;
        CommandPaletteQuickLookHost.Close();
        CommandPaletteOverlay.IsOpen = false;
        CommandPaletteSearchBox.Text = string.Empty;
    }

    internal void CommandPaletteOverlay_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_disposed) return;
        if (!IsCommandPalettePanelHit(e))
        {
            _host.ClosePalette();
            e.Handled = true;
        }
    }

    internal void CommandPaletteOverlay_Opened(object? sender, EventArgs e)
    {
        if (_disposed) return;
        _host.ApplyShortcutWindowOrder();
        ClampCommandPalettePanelPosition();
        QueueCommandPaletteSearchFocus();
    }

    internal void CommandPaletteDragHandle_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_disposed) return;
        ApplyCommandPalettePanelOffset(
            CommandPalettePanelTransform.X + e.HorizontalChange,
            CommandPalettePanelTransform.Y + e.VerticalChange);
        e.Handled = true;
    }

    internal void CommandPaletteLayout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_disposed) return;
        ClampCommandPalettePanelPosition();
    }

    private void ClampCommandPalettePanelPosition()
    {
        ApplyCommandPalettePanelOffset(
            CommandPalettePanelTransform.X,
            CommandPalettePanelTransform.Y);
    }

    private void ApplyCommandPalettePanelOffset(double horizontalOffset, double verticalOffset)
    {
        var availableHorizontalOffset = Math.Max(
            0,
            (CommandPaletteOverlaySurface.ActualWidth - CommandPalettePanel.ActualWidth) / 2
                - CommandPalettePanelEdgeMargin);
        var availableVerticalOffset = Math.Max(
            0,
            (CommandPaletteOverlaySurface.ActualHeight - CommandPalettePanel.ActualHeight) / 2
                - CommandPalettePanelEdgeMargin);
        CommandPalettePanelTransform.X = Math.Clamp(
            horizontalOffset,
            -availableHorizontalOffset,
            availableHorizontalOffset);
        CommandPalettePanelTransform.Y = Math.Clamp(
            verticalOffset,
            -availableVerticalOffset,
            availableVerticalOffset);
    }

    internal void CommandPaletteOverlay_Closed(object? sender, EventArgs e)
    {
        if (_disposed) return;
        ResetCommandPaletteInput();
        _commandPaletteSearchRefreshTimer.Stop();
        _host.CloseContext();
        _isCommandPaletteImeCompositionActive = false;
        _activeCommandPaletteSearchAliasItemId = null;
        _host.ResetShortcutWindowOrder();
        CommandPaletteQuickLookHost.Close();
        CommandPaletteSearchBox.Text = string.Empty;
    }

    internal bool IsCommandPalettePanelHit(MouseButtonEventArgs e)
    {
        if (_disposed) return false;
        if (FindVisualParent<QuickLookHost>(e.OriginalSource as DependencyObject) is not null)
        {
            return true;
        }

        var point = e.GetPosition(CommandPalettePanel);
        return point.X >= 0
            && point.Y >= 0
            && point.X <= CommandPalettePanel.ActualWidth
            && point.Y <= CommandPalettePanel.ActualHeight;
    }

    internal bool TryHandleCommandPaletteKey(KeyEventArgs e)
    {
        if (_disposed) return false;
        if (CommandPaletteKeyboardService.ShouldLetImeHandleKey(
                e.Key,
                e.SystemKey,
                _isCommandPaletteImeCompositionActive))
        {
            return false;
        }

        var key = CommandPaletteKeyboardService.GetCommandKey(e.Key, e.SystemKey);
        switch (key)
        {
            case Key.Escape:
                e.Handled = true;
                if (CommandPaletteQuickLookHost.IsOpen)
                {
                    CommandPaletteQuickLookHost.Close();
                    return true;
                }

                _host.ClosePalette();
                return true;
            case Key.Down:
            case Key.Up:
            case Key.Home:
            case Key.End:
            case Key.PageDown:
            case Key.PageUp:
                e.Handled = true;
                MoveCommandPaletteSelection(key);
                return true;
            case Key.Enter:
                e.Handled = true;
                if (CommandPaletteList.SelectedItem is CommandPaletteItem item)
                {
                    _host.QueueExecution(
                        CommandPaletteKeyboardService.GetExecutionMode(e.Key, e.SystemKey, Keyboard.Modifiers), item);
                }
                return true;
            default:
                return false;
        }
    }

    internal void QueueCommandPaletteSearchFocus()
    {
        if (!IsCommandPaletteOpen || _isCommandPaletteImeCompositionActive
            || CommandPaletteSearchBox.IsKeyboardFocusWithin
            || _commandPaletteFocusOperation?.Status == DispatcherOperationStatus.Pending)
        {
            return;
        }

        var generation = _commandPaletteInputGeneration;
        _commandPaletteFocusOperation = Dispatcher.InvokeAsync(() =>
        {
            _commandPaletteFocusOperation = null;
            if (generation == _commandPaletteInputGeneration) FocusCommandPaletteSearchBox();
        }, DispatcherPriority.ContextIdle);
    }

    internal void RefreshCommandPaletteItems()
    {
        if (_disposed) return;
        var selectedItemId = (CommandPaletteList.SelectedItem as CommandPaletteItem)?.Id;
        var previousSearchAliasItemId = _activeCommandPaletteSearchAliasItemId;
        var searchAliasItem = _host.CreateSearchAliasItem(CommandPaletteSearchBox.Text);
        _activeCommandPaletteSearchAliasItemId = searchAliasItem?.Id;
        _commandPaletteItems.Clear();
        var searchResults = _host.Search(CommandPaletteSearchBox.Text, _commandPaletteShowUnavailable);
        if (_commandPaletteCommandsOnly)
        {
            searchResults = searchResults
                .Where(static item => item.Type == CommandPaletteItemType.Command)
                .ToList();
        }

        if (searchAliasItem is not null)
        {
            searchResults = searchResults
                .Where(item => !string.Equals(item.Id, searchAliasItem.Id, StringComparison.Ordinal))
                .Prepend(searchAliasItem)
                .ToList();
        }

        foreach (var item in searchResults)
        {
            _commandPaletteItems.Add(item);
        }

        CommandPaletteNoResultsText.Visibility = _commandPaletteItems.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        var shouldSelectNewSearchAlias = searchAliasItem is not null
            && !string.Equals(
                previousSearchAliasItemId,
                searchAliasItem.Id,
                StringComparison.Ordinal);
        CommandPaletteList.SelectedItem = shouldSelectNewSearchAlias
            ? searchAliasItem
            : string.IsNullOrEmpty(selectedItemId)
                ? null
                : _commandPaletteItems.FirstOrDefault(item => item.Id == selectedItemId);
        if (CommandPaletteList.SelectedItem is null)
        {
            CommandPaletteList.SelectedIndex = _commandPaletteItems.Count > 0 ? 0 : -1;
        }

        if (CommandPaletteList.SelectedItem is not null)
        {
            CommandPaletteList.ScrollIntoView(CommandPaletteList.SelectedItem);
        }

        RefreshOpenCommandPaletteQuickLook();
        _host.QueueApplicationIconLoad();
    }

    private void MoveCommandPaletteSelection(Key key)
    {
        var index = CommandPaletteKeyboardService.GetSelectionIndex(
            key,
            CommandPaletteList.SelectedIndex,
            _commandPaletteItems.Count,
            CommandPalettePageSelectionCount);
        if (index < 0)
        {
            return;
        }

        CommandPaletteList.SelectedIndex = index;
        CommandPaletteList.ScrollIntoView(CommandPaletteList.SelectedItem);
    }

    internal void RecordCommandPaletteUsage(CommandPaletteItem item)
    {
        if (_disposed) return;
        if (item.Type is CommandPaletteItemType.History
                or CommandPaletteItemType.RecentlyClosed
                or CommandPaletteItemType.Clipboard
            || string.IsNullOrWhiteSpace(item.Id))
        {
            return;
        }

        _host.Usage = CommandPaletteUsageSettingsProfile.Record(
            _host.Usage,
            item.Id,
            DateTimeOffset.UtcNow);
        _commandPaletteUsageSaveTimer.Stop();
        _commandPaletteUsageSaveTimer.Start();
    }

    internal void CommandPaletteSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_disposed) return;
        if (IsCommandPaletteOpen)
        {
            QueueCommandPaletteRefresh();
        }
    }

    private void CommandPaletteSearchBox_PreviewTextInputStart(object sender, TextCompositionEventArgs e)
    {
        _isCommandPaletteImeCompositionActive = true;
        CancelCommandPaletteSearchFocus();
    }

    private void CommandPaletteSearchBox_PreviewTextInputUpdate(object sender, TextCompositionEventArgs e)
    {
        _isCommandPaletteImeCompositionActive = true;
        CancelCommandPaletteSearchFocus();
    }

    private void CommandPaletteSearchBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        _isCommandPaletteImeCompositionActive = false;
        QueuePendingCommandPaletteTextInput();
    }

    internal void CommandPaletteSearchBox_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (_disposed) return;
        if (e.Key is Key.Enter or Key.Escape)
        {
            _isCommandPaletteImeCompositionActive = false;
            QueuePendingCommandPaletteTextInput();
        }
    }

    internal void CommandPaletteSearchBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_disposed) return;
        _isCommandPaletteImeCompositionActive = false;
        QueuePendingCommandPaletteTextInput();
    }

    internal void QueueCommandPaletteRefresh()
    {
        if (_disposed) return;
        _commandPaletteSearchRefreshTimer.Stop();
        _commandPaletteSearchRefreshTimer.Start();
    }

    internal void CommandPaletteList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_disposed) return;
        if (FindVisualParent<Button>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        if (FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject) is not { DataContext: CommandPaletteItem item })
        {
            return;
        }

        e.Handled = true;
        CommandPaletteList.SelectedItem = item;
        _host.QueueExecution(CommandPaletteExecutionMode.Default, item);
    }

    internal void CommandPaletteList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_disposed) return;
        if (!_suppressCommandPaletteQuickLookSelectionRefresh)
        {
            RefreshOpenCommandPaletteQuickLook();
        }
    }

    internal async void CommandPalettePreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed) return;
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not CommandPaletteItem item)
        {
            return;
        }

        CommandPaletteList.SelectedItem = item;
        await ShowCommandPaletteQuickLookAsync();
    }

    private bool IsCommandPaletteQuickLookEnabled()
    {
        return _host.IsQuickLookEnabled;
    }

    private async Task ShowCommandPaletteQuickLookAsync()
    {
        if (!IsCommandPaletteQuickLookEnabled()
            || _commandPaletteQuickLookSource is not { } source
            || source.Current is null)
        {
            return;
        }

        await CommandPaletteQuickLookHost.ShowAsync(new QuickLookRequest(
            [source.Current],
            source.SourceId,
            source));
    }

    private void RefreshOpenCommandPaletteQuickLook()
    {
        if (CommandPaletteQuickLookHost.IsOpen)
        {
            _ = ShowCommandPaletteQuickLookAsync();
        }
    }

    private IReadOnlyList<QuickLookItem> CreateCommandPaletteQuickLookItems()
    {
        return _commandPaletteItems.Select(CreateCommandPaletteQuickLookItem).ToList();
    }

    private IReadOnlyList<QuickLookItem> CreateSelectedCommandPaletteQuickLookItems()
    {
        return CommandPaletteList.SelectedItem is CommandPaletteItem item
            ? [CreateCommandPaletteQuickLookItem(item)]
            : [];
    }

    private QuickLookItem CreateCommandPaletteQuickLookItem(CommandPaletteItem item)
    {
        if (item.Payload is Models.Workspaces.WorkspaceItem workspaceItem)
        {
            return QuickLookItemFactory.FromWorkspaceItem(
                workspaceItem,
                _commandPaletteQuickLookSource!.SourceId,
                item.Title);
        }

        return QuickLookItemFactory.FromAddress(
            item.Title,
            item.Url,
            _commandPaletteQuickLookSource!.SourceId,
            string.IsNullOrWhiteSpace(item.Subtitle) ? item.TypeText : item.Subtitle,
            thumbnail: item.Thumbnail,
            favicon: item.Icon);
    }

    private void SelectCommandPaletteQuickLookIndex(int index)
    {
        if (index < 0 || index >= _commandPaletteItems.Count)
        {
            return;
        }

        _suppressCommandPaletteQuickLookSelectionRefresh = true;
        try
        {
            CommandPaletteList.SelectedIndex = index;
            CommandPaletteList.ScrollIntoView(CommandPaletteList.SelectedItem);
        }
        finally
        {
            _suppressCommandPaletteQuickLookSelectionRefresh = false;
        }
    }

    internal void InsertCommandPaletteSearchText(string text)
    {
        if (_disposed || _isCommandPaletteImeCompositionActive) return;

        var currentText = CommandPaletteSearchBox.Text ?? string.Empty;
        var selectionStart = Math.Clamp(CommandPaletteSearchBox.SelectionStart, 0, currentText.Length);
        var selectionLength = Math.Clamp(CommandPaletteSearchBox.SelectionLength, 0, currentText.Length - selectionStart);
        CommandPaletteSearchBox.Text = currentText.Remove(selectionStart, selectionLength).Insert(selectionStart, text);
        CommandPaletteSearchBox.CaretIndex = selectionStart + text.Length;
    }

    internal void BackspaceCommandPaletteSearchText()
    {
        if (_disposed || _isCommandPaletteImeCompositionActive) return;

        var currentText = CommandPaletteSearchBox.Text ?? string.Empty;
        var selectionStart = Math.Clamp(CommandPaletteSearchBox.SelectionStart, 0, currentText.Length);
        var selectionLength = Math.Clamp(CommandPaletteSearchBox.SelectionLength, 0, currentText.Length - selectionStart);
        if (selectionLength > 0)
        {
            CommandPaletteSearchBox.Text = currentText.Remove(selectionStart, selectionLength);
            CommandPaletteSearchBox.CaretIndex = selectionStart;
            return;
        }

        if (selectionStart <= 0)
        {
            return;
        }

        CommandPaletteSearchBox.Text = currentText.Remove(selectionStart - 1, 1);
        CommandPaletteSearchBox.CaretIndex = selectionStart - 1;
    }

    internal void CommandPaletteShowAllCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_disposed) return;
        _commandPaletteShowUnavailable = CommandPaletteShowAllCheckBox.IsChecked == true;
        if (IsCommandPaletteOpen)
        {
            RefreshCommandPaletteItems();
        }
    }

    internal void CommandPaletteCommandsOnlyCheckBox_Changed(
        object sender,
        RoutedEventArgs e)
    {
        if (_disposed) return;
        _commandPaletteCommandsOnly =
            CommandPaletteCommandsOnlyCheckBox.IsChecked == true;
        if (IsCommandPaletteOpen)
        {
            RefreshCommandPaletteItems();
        }
    }

    private void FocusCommandPaletteSearchBox()
    {
        if (!IsCommandPaletteOpen || _isCommandPaletteImeCompositionActive
            || CommandPaletteSearchBox.IsKeyboardFocusWithin)
        {
            return;
        }

        if (PresentationSource.FromVisual(CommandPaletteSearchBox) is HwndSource popupSource
            && popupSource.Handle != IntPtr.Zero)
        {
            _host.SetFocus(popupSource.Handle);
        }

        // HWNDのfocus移動中に始まったIME入力にも割り込まない。
        if (IsCommandPaletteOpen && !_isCommandPaletteImeCompositionActive
            && !CommandPaletteSearchBox.IsKeyboardFocusWithin)
        {
            CommandPaletteSearchBox.Focusable = true;
            CommandPaletteSearchBox.Focus();
        }
    }

    internal void QueueCommandPaletteTextInput(string? text)
    {
        if (!IsCommandPaletteOpen) return;
        // nullはBackspace。入力はこの表示中だけ保持し、閉じたら破棄する。
        _pendingCommandPaletteTextInput.Enqueue(text);
        QueuePendingCommandPaletteTextInput();
    }

    private void QueuePendingCommandPaletteTextInput()
    {
        if (!IsCommandPaletteOpen || _isCommandPaletteImeCompositionActive
            || _pendingCommandPaletteTextInput.Count == 0
            || _commandPaletteTextInputOperation?.Status == DispatcherOperationStatus.Pending)
        {
            return;
        }

        CancelCommandPaletteSearchFocus();
        var generation = _commandPaletteInputGeneration;
        _commandPaletteTextInputOperation = Dispatcher.InvokeAsync(() =>
        {
            _commandPaletteTextInputOperation = null;
            if (!CanApplyCommandPaletteTextInput(generation)) return;
            // nativeの文字通知内でfocus/Textを変更しない。TextStoreの準備も先に終える。
            FocusCommandPaletteSearchBox();
            if (!CanApplyCommandPaletteTextInput(generation)) return;
            _commandPaletteTextInputOperation = Dispatcher.InvokeAsync(() =>
            {
                _commandPaletteTextInputOperation = null;
                while (CanApplyCommandPaletteTextInput(generation)
                    && _pendingCommandPaletteTextInput.TryDequeue(out var input))
                {
                    if (input is null) BackspaceCommandPaletteSearchText();
                    else InsertCommandPaletteSearchText(input);
                }
            }, DispatcherPriority.ContextIdle);
        }, DispatcherPriority.ContextIdle);
    }

    private bool CanApplyCommandPaletteTextInput(int generation)
        => IsCommandPaletteOpen && !_isCommandPaletteImeCompositionActive
            && generation == _commandPaletteInputGeneration;

    private void CancelCommandPaletteSearchFocus()
    {
        _commandPaletteFocusOperation?.Abort();
        _commandPaletteFocusOperation = null;
    }

    private void ResetCommandPaletteInput()
    {
        _commandPaletteInputGeneration++;
        CancelCommandPaletteSearchFocus();
        _commandPaletteTextInputOperation?.Abort();
        _commandPaletteTextInputOperation = null;
        _pendingCommandPaletteTextInput.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ResetCommandPaletteInput();
        _commandPaletteSearchRefreshTimer.Stop();
        _commandPaletteUsageSaveTimer.Stop();
        _commandPaletteSearchRefreshTimer.Tick -= SearchRefreshTimer_Tick;
        _commandPaletteUsageSaveTimer.Tick -= UsageSaveTimer_Tick;
        TextCompositionManager.RemovePreviewTextInputStartHandler(CommandPaletteSearchBox, CommandPaletteSearchBox_PreviewTextInputStart);
        TextCompositionManager.RemovePreviewTextInputUpdateHandler(CommandPaletteSearchBox, CommandPaletteSearchBox_PreviewTextInputUpdate);
        TextCompositionManager.RemovePreviewTextInputHandler(CommandPaletteSearchBox, CommandPaletteSearchBox_PreviewTextInput);
        _isCommandPaletteImeCompositionActive = false;
        _activeCommandPaletteSearchAliasItemId = null;
        _commandPaletteQuickLookSource = null;
        CommandPaletteList.ItemsSource = null;
        _commandPaletteItems.Clear();
    }

    private static T? FindVisualParent<T>(DependencyObject? source)
        where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T target)
            {
                return target;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }
}
