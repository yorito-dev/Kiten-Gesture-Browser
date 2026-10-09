using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TabBrowser.Models;
using TabBrowser.Services;

namespace TabBrowser.Views;

internal sealed class AddressSuggestionItem(
    AddressSuggestionSource source,
    Guid sourceId,
    BrowsingHistoryEntry? historyEntry)
{
    public AddressSuggestionKind Kind => source.Kind;

    public Guid SourceId { get; } = sourceId;

    public BrowsingHistoryEntry? HistoryEntry { get; } = historyEntry;

    public string StableKey => source.StableKey;

    public string KindLabel => source.Kind switch
    {
        AddressSuggestionKind.OpenTab => "タブ",
        AddressSuggestionKind.Bookmark => "ブックマーク",
        _ => "履歴"
    };

    public string Title => source.Title;

    public string SecondaryText => source.SecondaryText;

    public string Url => source.Url;

    public BrowsingHistoryEntry CreateHistoryEntry(string? containerProfileId = null)
    {
        return new BrowsingHistoryEntry(
            0,
            source.Url,
            source.Title,
            source.Host,
            containerProfileId ?? source.ContainerProfileId,
            BrowsingHistorySourceKind.WebTab,
            DateTimeOffset.Now);
    }
}

internal sealed record AddressBarElements(
    TextBox AddressBox,
    ListBox AddressSuggestionList,
    Popup AddressSuggestionPopup,
    MenuItem AddressPasteAndNavigateMenuItem);

// タブ・履歴・設定・実遷移は既存のWindow側で解決する。
internal interface IAddressBarHost
{
    bool IsPrivateWindow { get; }

    bool IsBrowsingHistoryEnabled { get; }

    bool TryGetDragUrl(out string url);

    void StartUrlDrag(string url);

    bool IsFolderNavigation(string input);

    bool IsSearchInput(string input);

    Task NavigateAsync(string input);

    Task OpenSuggestionAsync(AddressSuggestionItem suggestion, BrowsingHistoryOpenMode mode);

    IReadOnlyList<AddressSuggestionCandidate> CaptureMemoryCandidates();
}

internal sealed class AddressBarController : IDisposable
{
    private readonly AddressBarElements _elements;
    private readonly IAddressBarHost _host;
    private readonly WindowTaskSupervisor _windowTaskSupervisor;
    private readonly AddressSuggestionQueryCoordinator _addressSuggestionQueryCoordinator;
    private readonly AddressBarInteractionCoordinator _addressBarInteractionCoordinator = new();
    private readonly ObservableCollection<AddressSuggestionItem> _addressSuggestions = [];

    internal AddressBarController(
        AddressBarElements elements,
        IAddressBarHost host,
        WindowTaskSupervisor taskSupervisor,
        AddressSuggestionQueryCoordinator queryCoordinator)
    {
        _elements = elements;
        _host = host;
        _windowTaskSupervisor = taskSupervisor;
        _addressSuggestionQueryCoordinator = queryCoordinator;
    }

    internal void InitializeSuggestions()
        => _elements.AddressSuggestionList.ItemsSource = _addressSuggestions;

    internal void BeginEdit()
    {
        _addressBarInteractionCoordinator.BeginEdit(_elements.AddressBox.Text);
        _elements.AddressBox.SelectAll();
    }

    public void Dispose() => _addressSuggestionQueryCoordinator.Dispose();

    internal void HandleMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var dragStartPoint = e.GetPosition(_elements.AddressBox);
        var isFocused = _elements.AddressBox.IsKeyboardFocusWithin;
        var isFullUrlSelected = _elements.AddressBox.Text.Length > 0 && _elements.AddressBox.SelectionLength == _elements.AddressBox.Text.Length;
        var isDragCandidate = _host.TryGetDragUrl(out _) && (!isFocused || isFullUrlSelected);
        _addressBarInteractionCoordinator.BeginDragCandidate(
            dragStartPoint.X,
            dragStartPoint.Y,
            isDragCandidate);
        if (_addressBarInteractionCoordinator.IsDragCandidate)
        {
            _elements.AddressBox.CaptureMouse();
        }

        if (isFocused)
        {
            return;
        }

        _elements.AddressBox.Focus();
        _elements.AddressBox.SelectAll();
        e.Handled = true;
    }

    internal void HandleMouseLeftButtonUp()
    {
        _addressBarInteractionCoordinator.CancelDragCandidate();
        _elements.AddressBox.ReleaseMouseCapture();
    }

    internal void HandleMouseMove(MouseEventArgs e)
    {
        if (!_addressBarInteractionCoordinator.IsDragCandidate)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _addressBarInteractionCoordinator.CancelDragCandidate();
            _elements.AddressBox.ReleaseMouseCapture();
            return;
        }

        var currentPoint = e.GetPosition(_elements.AddressBox);
        if (!_addressBarInteractionCoordinator.HasExceededDragThreshold(
                currentPoint.X,
                currentPoint.Y,
                SystemParameters.MinimumHorizontalDragDistance,
                SystemParameters.MinimumVerticalDragDistance))
        {
            return;
        }

        if (_elements.AddressBox.SelectionLength > 0 && _elements.AddressBox.SelectionLength < _elements.AddressBox.Text.Length)
        {
            return;
        }

        if (!_host.TryGetDragUrl(out var url))
        {
            _addressBarInteractionCoordinator.CancelDragCandidate();
            _elements.AddressBox.ReleaseMouseCapture();
            return;
        }

        _addressBarInteractionCoordinator.CancelDragCandidate();
        _elements.AddressBox.ReleaseMouseCapture();
        CloseAddressSuggestions();
        e.Handled = true;
        _host.StartUrlDrag(url);
    }

    internal async Task HandleKeyDownAsync(KeyEventArgs e)
    {
        if (HandleAddressSuggestionKey(e))
        {
            return;
        }

        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        CloseAddressSuggestions();
        await _host.NavigateAsync(_elements.AddressBox.Text);
    }

    internal void OpenContextMenu(Func<string?> readPasteText)
    {
        CloseAddressSuggestions();
        _addressBarInteractionCoordinator.SetPasteNavigationText(readPasteText());

        var isFolderNavigation = _addressBarInteractionCoordinator.PasteNavigationText is { } input
            && _host.IsFolderNavigation(input);
        var isSearch = _addressBarInteractionCoordinator.PasteNavigationText is { } searchInput
            && !isFolderNavigation
            && _host.IsSearchInput(searchInput);
        _elements.AddressPasteAndNavigateMenuItem.Header = isSearch
            ? "貼り付けて検索"
            : "貼り付けて移動";
        _elements.AddressPasteAndNavigateMenuItem.IsEnabled = _addressBarInteractionCoordinator.PasteNavigationText is not null;
    }

    internal void CloseContextMenu()
    {
        _addressBarInteractionCoordinator.ClearPasteNavigationText();
    }

    internal async Task PasteAndNavigateAsync()
    {
        var input = _addressBarInteractionCoordinator.PasteNavigationText;
        if (input is null)
        {
            return;
        }

        SetAddressText(input);
        _elements.AddressBox.CaretIndex = _elements.AddressBox.Text.Length;
        CloseAddressSuggestions();
        await _host.NavigateAsync(_elements.AddressBox.Text);
    }

    internal async Task HandleTextChangedAsync()
    {
        if (_addressBarInteractionCoordinator.IsSuggestionSuppressed)
        {
            return;
        }

        await UpdateAddressSuggestionsAsync();
    }

    private bool HandleAddressSuggestionKey(KeyEventArgs e)
    {
        if (e.Key == Key.Escape
            && (_elements.AddressSuggestionPopup.IsOpen
                || _addressBarInteractionCoordinator.HasEditChanged(_elements.AddressBox.Text)))
        {
            e.Handled = true;
            RestoreAddressBeforeSuggestionEdit();
            return true;
        }

        if (!_elements.AddressSuggestionPopup.IsOpen || _addressSuggestions.Count == 0)
        {
            return false;
        }

        switch (e.Key)
        {
            case Key.Down:
                MoveAddressSuggestionSelection(1);
                e.Handled = true;
                return true;
            case Key.Up:
                MoveAddressSuggestionSelection(-1);
                e.Handled = true;
                return true;
            case Key.Enter when _elements.AddressSuggestionList.SelectedItem is AddressSuggestionItem:
                e.Handled = true;
                QueueAddressSuggestionOpen(GetAddressSuggestionOpenMode(Keyboard.Modifiers));
                return true;
            default:
                return false;
        }
    }

    private void MoveAddressSuggestionSelection(int delta)
    {
        if (_addressSuggestions.Count == 0)
        {
            return;
        }

        var index = _elements.AddressSuggestionList.SelectedIndex;
        if (index < 0)
        {
            index = delta > 0 ? -1 : 0;
        }

        index = Math.Clamp(index + delta, 0, _addressSuggestions.Count - 1);
        _elements.AddressSuggestionList.SelectedIndex = index;
        _elements.AddressSuggestionList.ScrollIntoView(_elements.AddressSuggestionList.SelectedItem);
    }

    private void QueueAddressSuggestionOpen(BrowsingHistoryOpenMode mode)
    {
        _windowTaskSupervisor.TryRun(
            "address-suggestion-open",
            _ => OpenSelectedAddressSuggestionAsync(mode));
    }

    private async Task OpenSelectedAddressSuggestionAsync(BrowsingHistoryOpenMode mode)
    {
        if (_elements.AddressSuggestionList.SelectedItem is not AddressSuggestionItem suggestion)
        {
            return;
        }

        await _host.OpenSuggestionAsync(suggestion, mode);
    }

    private Task UpdateAddressSuggestionsAsync()
    {
        var query = _elements.AddressBox.Text.Trim();
        if (!_elements.AddressBox.IsKeyboardFocusWithin || query.Length == 0)
        {
            CloseAddressSuggestions();
            return Task.CompletedTask;
        }

        return _addressSuggestionQueryCoordinator.UpdateAsync(
            query,
            _host.CaptureMemoryCandidates,
            _host.IsPrivateWindow,
            _host.IsBrowsingHistoryEnabled,
            ApplyAddressSuggestions);
    }

    private void ApplyAddressSuggestions(AddressSuggestionQueryResult result)
    {
        if (!_addressSuggestionQueryCoordinator.IsCurrent(result)
            || !_elements.AddressBox.IsKeyboardFocusWithin
            || !string.Equals(_elements.AddressBox.Text.Trim(), result.Query, StringComparison.Ordinal))
        {
            return;
        }

        var selectedKey = (_elements.AddressSuggestionList.SelectedItem as AddressSuggestionItem)?.StableKey;
        _addressSuggestions.Clear();
        foreach (var candidate in result.Candidates)
        {
            _addressSuggestions.Add(new AddressSuggestionItem(candidate.Source, candidate.SourceId, candidate.HistoryEntry));
        }

        _elements.AddressSuggestionList.SelectedItem = selectedKey is null
            ? null
            : _addressSuggestions.FirstOrDefault(item => item.StableKey == selectedKey);
        _elements.AddressSuggestionPopup.IsOpen = _addressSuggestions.Count > 0;
    }

    private void RestoreAddressBeforeSuggestionEdit()
    {
        CloseAddressSuggestions();
        SetAddressText(_addressBarInteractionCoordinator.OriginalEditText);
        _elements.AddressBox.SelectAll();
    }

    internal void CloseAddressSuggestions()
    {
        _addressSuggestionQueryCoordinator.Cancel();
        _elements.AddressSuggestionPopup.IsOpen = false;
        _elements.AddressSuggestionList.SelectedItem = null;
        _addressSuggestions.Clear();
    }

    internal void HandlePopupClosed()
    {
        // 候補0件への反映によるcloseは、後続の履歴候補を待つため無効化しない。
        _addressSuggestionQueryCoordinator.OnPopupClosed(_addressSuggestions.Count > 0);
    }

    private static BrowsingHistoryOpenMode GetAddressSuggestionOpenMode(ModifierKeys modifiers)
    {
        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            return BrowsingHistoryOpenMode.Split;
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            return BrowsingHistoryOpenMode.BackgroundTab;
        }

        return modifiers.HasFlag(ModifierKeys.Control)
            ? BrowsingHistoryOpenMode.NewTab
            : BrowsingHistoryOpenMode.CurrentTab;
    }

    internal void SetAddressText(string text)
    {
        using (_addressBarInteractionCoordinator.EnterSuggestionSuppression())
        {
            _elements.AddressBox.Text = text;
        }

        CloseAddressSuggestions();
    }

}
