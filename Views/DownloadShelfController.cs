using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using TabBrowser.Models;
using TabBrowser.Services;

namespace TabBrowser.Views;

internal sealed record DownloadShelfElements(
    ComboBox DownloadKindFilterBox,
    TextBox DownloadSearchBox,
    ItemsControl DownloadList,
    Button DownloadsButton,
    Popup DownloadShelfPopup);

// 一覧表示とその購読を所有する。取得処理、危険ファイル確認、実ファイル操作は担当しない。
internal sealed class DownloadShelfController : IDisposable
{
    private readonly DownloadService _downloadService;
    private readonly DownloadShelfElements _elements;
    private readonly Func<DownloadSettings> _getSettings;
    private readonly Action<Action> _queueUiUpdate;
    private readonly HashSet<DownloadItem> _subscribedItems = [];
    private ICollectionView? _downloadItemsView;
    private bool _initialized;
    private bool _disposed;

    internal DownloadShelfController(
        DownloadService downloadService,
        DownloadShelfElements elements,
        Func<DownloadSettings> getSettings,
        Action<Action> queueUiUpdate)
    {
        _downloadService = downloadService;
        _elements = elements;
        _getSettings = getSettings;
        _queueUiUpdate = queueUiUpdate;
    }

    internal void Initialize()
    {
        if (_initialized || _disposed) return;
        _initialized = true;
        _downloadService.ItemFinished += ItemFinished;
        _elements.DownloadKindFilterBox.ItemsSource = new List<DownloadFilterOption>
        {
            new("すべて", "all"),
            new("PDF", nameof(DownloadFileKind.Pdf)),
            new("画像", nameof(DownloadFileKind.Image)),
            new("圧縮", nameof(DownloadFileKind.Archive)),
            new("実行形式", nameof(DownloadFileKind.Executable)),
            new("動画", nameof(DownloadFileKind.Video)),
            new("その他", nameof(DownloadFileKind.Other))
        };
        _elements.DownloadKindFilterBox.SelectedValue = "all";

        _downloadItemsView = CollectionViewSource.GetDefaultView(_downloadService.Items);
        _downloadItemsView.Filter = FilterDownloadItem;
        _elements.DownloadList.ItemsSource = _downloadItemsView;
        foreach (var item in _downloadService.Items)
        {
            if (_subscribedItems.Add(item)) item.PropertyChanged += DownloadItem_PropertyChanged;
        }

        _downloadService.Items.CollectionChanged += DownloadItems_CollectionChanged;
        UpdateButtonState();
    }

    private void DownloadItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var item in _subscribedItems) item.PropertyChanged -= DownloadItem_PropertyChanged;
            _subscribedItems.Clear();
            foreach (var item in _downloadService.Items)
            {
                if (_subscribedItems.Add(item)) item.PropertyChanged += DownloadItem_PropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (DownloadItem item in e.NewItems)
            {
                if (_subscribedItems.Add(item)) item.PropertyChanged += DownloadItem_PropertyChanged;
            }
        }

        if (e.OldItems is not null)
        {
            foreach (DownloadItem item in e.OldItems)
            {
                if (!_downloadService.Items.Contains(item) && _subscribedItems.Remove(item))
                    item.PropertyChanged -= DownloadItem_PropertyChanged;
            }
        }

        UpdateButtonState();
    }

    private void DownloadItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DownloadItem.State) or nameof(DownloadItem.CanResumeOperation))
        {
            UpdateButtonState();
        }
    }

    private bool FilterDownloadItem(object value)
    {
        if (value is not DownloadItem item)
        {
            return false;
        }

        var selectedKind = _elements.DownloadKindFilterBox.SelectedValue as string;
        if (!string.IsNullOrWhiteSpace(selectedKind)
            && !selectedKind.Equals("all", StringComparison.OrdinalIgnoreCase)
            && !item.Kind.ToString().Equals(selectedKind, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var query = _elements.DownloadSearchBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        return item.FileName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || item.SourceUrl.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.FilePath.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    internal void UpdateButtonState()
    {
        if (_disposed) return;
        var activeCount = _downloadService.Items.Count(static item =>
            item.State is DownloadItemState.Pending or DownloadItemState.InProgress or DownloadItemState.Paused);
        _elements.DownloadsButton.Content = activeCount > 0
            ? $"DL{Math.Min(activeCount, 9)}"
            : "DL";
        _elements.DownloadsButton.ToolTip = activeCount > 0
            ? $"ダウンロード中: {activeCount} 件"
            : "ダウンロード";
    }

    internal void Refresh()
    {
        if (!_disposed) _downloadItemsView?.Refresh();
    }

    internal void Toggle()
    {
        if (_disposed) return;
        Refresh();
        _elements.DownloadShelfPopup.IsOpen = !_elements.DownloadShelfPopup.IsOpen;
    }

    internal void Open()
    {
        if (_disposed) return;
        Refresh();
        _elements.DownloadShelfPopup.IsOpen = true;
    }

    internal void OpenForStart()
    {
        if (!_disposed && _getSettings().OpenShelfOnDownloadStart)
            _elements.DownloadShelfPopup.IsOpen = true;
    }

    internal void OpenForCompletion()
    {
        if (!_disposed && _getSettings().OpenShelfOnDownloadCompleted)
            _elements.DownloadShelfPopup.IsOpen = true;
    }

    private void ItemFinished(DownloadItem item)
    {
        if (_disposed) return;
        _queueUiUpdate(() =>
        {
            if (_disposed) return;
            UpdateButtonState();
            OpenForCompletion();
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _downloadService.ItemFinished -= ItemFinished;
        _downloadService.Items.CollectionChanged -= DownloadItems_CollectionChanged;
        foreach (var item in _subscribedItems) item.PropertyChanged -= DownloadItem_PropertyChanged;
        _subscribedItems.Clear();
        if (_downloadItemsView is not null) _downloadItemsView.Filter = null;
        _elements.DownloadList.ItemsSource = null;
        _downloadItemsView = null;
        _elements.DownloadShelfPopup.IsOpen = false;
    }

    private sealed class DownloadFilterOption(string label, string value)
    {
        public string Label { get; } = label;

        public string Value { get; } = value;
    }

}
