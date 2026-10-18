using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
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
    Popup DownloadShelfPopup,
    TextBlock DownloadsButtonText,
    ProgressBar DownloadsButtonProgress);

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
        if (e.PropertyName is nameof(DownloadItem.State) or nameof(DownloadItem.CanResumeOperation)
            or nameof(DownloadItem.ProgressPercent))
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
        var activeItems = _downloadService.Items.Where(static item =>
            item.State is DownloadItemState.Pending or DownloadItemState.InProgress or DownloadItemState.Paused).ToArray();
        var unknownCount = activeItems.Count(static item =>
            (item.ProgressPercentOverride is null && item.TotalBytesToReceive <= 0)
            || !double.IsFinite(item.ProgressPercent));
        var hasProgress = activeItems.Length > 0 && unknownCount == 0;
        var average = hasProgress ? activeItems.Average(static item => item.ProgressPercent) : 0;
        var progressText = hasProgress ? $"{average:0}%" : "…";
        var label = activeItems.Length > 0 ? $"DL{Math.Min(activeItems.Length, 9)} {progressText}" : "DL";
        var toolTip = activeItems.Length == 0
            ? "ダウンロード"
            : $"ダウンロード中: {activeItems.Length} 件\n"
                + (hasProgress ? $"平均進捗: {progressText}" : $"進捗不明: {unknownCount} 件");
        if (_elements.DownloadsButtonText.Text != label) _elements.DownloadsButtonText.Text = label;
        if (!Equals(_elements.DownloadsButton.ToolTip, toolTip)) _elements.DownloadsButton.ToolTip = toolTip;
        _elements.DownloadsButtonProgress.Value = average;
        _elements.DownloadsButtonProgress.IsIndeterminate = activeItems.Length > 0 && !hasProgress;
        _elements.DownloadsButtonProgress.Visibility = activeItems.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
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
        _elements.DownloadsButtonProgress.IsIndeterminate = false;
        _elements.DownloadsButtonProgress.Visibility = Visibility.Collapsed;
    }

    private sealed class DownloadFilterOption(string label, string value)
    {
        public string Label { get; } = label;

        public string Value { get; } = value;
    }

}
