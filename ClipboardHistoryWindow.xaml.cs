using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using TabBrowser.Models;
using TabBrowser.Services;

namespace TabBrowser;

public partial class ClipboardHistoryWindow : Window
{
    private readonly ClipboardHistoryService _service;
    private readonly ICollectionView _itemsView;

    public ClipboardHistoryWindow(ClipboardHistoryService service)
    {
        _service = service;
        InitializeComponent();

        var source = new CollectionViewSource { Source = service.Items };
        _itemsView = source.View;
        _itemsView.Filter = MatchesSearch;
        HistoryList.ItemsSource = _itemsView;
        service.ItemsChanged += Items_CollectionChanged;
        service.StateChanged += Service_StateChanged;
        UpdateState();
    }

    public Action<ClipboardHistoryItem>? OpenItemRequested { get; set; }

    public Action? OpenSettingsRequested { get; set; }

    protected override void OnClosed(EventArgs e)
    {
        _service.ItemsChanged -= Items_CollectionChanged;
        _service.StateChanged -= Service_StateChanged;
        base.OnClosed(e);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _itemsView.Refresh();
        UpdateState();
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        _service.SetPaused(!_service.IsPaused);
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _service.Clear();
        StatusText.Text = "クリップボード履歴を消去しました。";
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        OpenSettingsRequested?.Invoke();
    }

    private void CopyItemButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ClipboardHistoryItem item)
        {
            CopyItem(item);
        }
    }

    private void OpenItemButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ClipboardHistoryItem item || !item.CanOpen)
        {
            return;
        }

        try
        {
            OpenItemRequested?.Invoke(item);
            StatusText.Text = "選択した項目を開きました。";
        }
        catch (Exception)
        {
            StatusText.Text = "選択した項目を開けませんでした。参照先を確認してください。";
        }
    }

    private void DeleteItemButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ClipboardHistoryItem item)
        {
            _service.Remove(item.Id);
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        if (SearchBox.IsKeyboardFocusWithin)
        {
            return;
        }

        if (e.Key == Key.Delete && HistoryList.SelectedItem is ClipboardHistoryItem deleteItem)
        {
            _service.Remove(deleteItem.Id);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && HistoryList.SelectedItem is ClipboardHistoryItem copyItem)
        {
            CopyItem(copyItem);
            e.Handled = true;
        }
    }

    private void CopyItem(ClipboardHistoryItem item)
    {
        var result = _service.CopyToClipboard(item);
        StatusText.Text = result.Message;
    }

    private bool MatchesSearch(object item)
    {
        if (item is not ClipboardHistoryItem historyItem)
        {
            return false;
        }

        var query = SearchBox.Text.Trim();
        return query.Length == 0
            || historyItem.SearchText.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || historyItem.TypeText.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateState();
    }

    private void Service_StateChanged(object? sender, EventArgs e)
    {
        UpdateState();
    }

    private void UpdateState()
    {
        PauseButton.IsEnabled = _service.IsEnabled && _service.IsMonitoring;
        PauseButton.Content = _service.IsPaused ? "再開" : "一時停止";
        EmptyText.Visibility = _itemsView.IsEmpty ? Visibility.Visible : Visibility.Collapsed;

        if (!_service.IsEnabled)
        {
            StatusText.Text = "クリップボード履歴は設定で無効になっています。";
        }
        else if (_service.LastError is { } error)
        {
            StatusText.Text = error;
        }
        else if (_service.IsPaused)
        {
            StatusText.Text = $"監視を一時停止しています。表示中 {_itemsView.Cast<object>().Count()}件";
        }
        else
        {
            StatusText.Text = $"アプリ終了時に消去されます。表示中 {_itemsView.Cast<object>().Count()}件";
        }
    }
}
