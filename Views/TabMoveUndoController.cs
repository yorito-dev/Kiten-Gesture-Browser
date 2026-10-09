using System.Windows;
using System.Windows.Controls;
using TabBrowser.Models;
using TabBrowser.Services;

namespace TabBrowser.Views;

internal interface ITabMoveUndoHost
{
    bool IsClosing { get; }
    IReadOnlyList<BrowserTab> Tabs { get; }
    string StatusText { get; }
    void RestoreTabOrder(IReadOnlyList<BrowserTab> originalOrder);
    void QueueSessionSave();
    void RefreshVerticalTabViews();
    void SetStatus(string message, bool autoClear);
    void ClearStatus();
    bool TryInsertFrom(ITabMoveUndoHost source, BrowserTab tab, int insertionIndex);
    void Close();
    void Activate();
}

// 移動取り消しの状態・期限・復元手順を所有する。実転送は既存のWindow側transactionを使う。
internal sealed class TabMoveUndoController : IDisposable
{
    private const string ExternalTabMoveSourceStatus = "別ウィンドウに移動しました";
    private const string ExternalTabMoveTargetStatus = "別ウィンドウからタブを移動しました";
    private readonly ITabMoveUndoHost _host;
    private readonly Button _button;
    private readonly OwnedDispatcherTimer _timer;
    private UndoState? _state;
    private bool _isApplyingUndo;
    private bool _disposed;

    internal TabMoveUndoController(ITabMoveUndoHost host, Button button, OwnedDispatcherTimer timer)
    {
        _host = host;
        _button = button;
        _timer = timer;
        _timer.Tick += Timer_Tick;
    }

    internal void RegisterReorder(IReadOnlyList<BrowserTab>? originalOrder)
    {
        if (_disposed) return;
        if (_isApplyingUndo || originalOrder is null || originalOrder.SequenceEqual(_host.Tabs))
        {
            _host.SetStatus("タブ順序を変更しました", autoClear: true);
            return;
        }

        Show(new UndoState
        {
            OriginalOrder = originalOrder.ToList(),
            Source = this,
            Target = this
        }, "タブ順序を変更しました");
    }

    internal void RegisterExternalMove(
        TabMoveUndoController originalSource,
        bool isInteractiveDrag,
        BrowserTab targetTab,
        int originalInsertionIndex,
        bool closeTargetWindowAfterUndo = false)
    {
        if (_disposed) return;
        if (isInteractiveDrag && originalSource != this && !originalSource._host.IsClosing)
        {
            originalSource._host.SetStatus(ExternalTabMoveSourceStatus, autoClear: true);
        }

        if (_isApplyingUndo || !isInteractiveDrag)
        {
            _host.SetStatus(ExternalTabMoveTargetStatus, autoClear: true);
            return;
        }

        Show(new UndoState
        {
            Source = originalSource,
            Target = this,
            TargetTabId = targetTab.Id,
            SourceInsertionIndex = originalInsertionIndex,
            CloseTargetWindowAfterUndo = closeTargetWindowAfterUndo
        }, ExternalTabMoveTargetStatus);
    }

    private void Show(UndoState state, string message)
    {
        _state = state;
        _button.Visibility = Visibility.Visible;
        _timer.Stop();
        _timer.Start();
        _host.SetStatus(message, autoClear: false);
    }

    private void Timer_Tick(object? sender, EventArgs e) => Clear();

    internal void Clear()
    {
        if (_disposed) return;
        _timer.Stop();
        _state = null;
        _button.Visibility = Visibility.Collapsed;
        if (string.Equals(_host.StatusText, "タブ順序を変更しました", StringComparison.Ordinal)
            || string.Equals(_host.StatusText, ExternalTabMoveTargetStatus, StringComparison.Ordinal))
        {
            _host.ClearStatus();
        }
    }

    internal void Undo()
    {
        if (_disposed) return;
        var state = _state;
        Clear();
        if (state is null) return;

        if (state.OriginalOrder is { } originalOrder)
        {
            _isApplyingUndo = true;
            try
            {
                _host.RestoreTabOrder(originalOrder);
                _host.QueueSessionSave();
                _host.RefreshVerticalTabViews();
                _host.SetStatus("タブ順序を元に戻しました", autoClear: true);
            }
            finally
            {
                _isApplyingUndo = false;
            }
            return;
        }

        var source = state.Source;
        var target = state.Target;
        var targetTab = target?._host.Tabs.FirstOrDefault(tab => tab.Id == state.TargetTabId);
        if (source is null || target is null || targetTab is null
            || source._host.IsClosing || target._host.IsClosing
            || source._disposed || target._disposed)
        {
            _host.SetStatus("移動元へ戻せませんでした", autoClear: true);
            return;
        }

        source._isApplyingUndo = true;
        target._isApplyingUndo = true;
        try
        {
            var restored = source._host.TryInsertFrom(target._host, targetTab, state.SourceInsertionIndex);
            if (restored)
            {
                if (state.CloseTargetWindowAfterUndo && target._host.Tabs.All(static tab => tab.IsWorkspaceTab))
                {
                    target._host.Close();
                }
                source._host.SetStatus("タブ移動を元に戻しました", autoClear: true);
                source._host.Activate();
            }
            else
            {
                _host.SetStatus("移動元へ戻せませんでした", autoClear: true);
            }
        }
        finally
        {
            source._isApplyingUndo = false;
            target._isApplyingUndo = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
        _state = null;
        _button.Visibility = Visibility.Collapsed;
    }

    private sealed class UndoState
    {
        public IReadOnlyList<BrowserTab>? OriginalOrder { get; init; }
        public TabMoveUndoController? Source { get; init; }
        public TabMoveUndoController? Target { get; init; }
        public Guid TargetTabId { get; init; }
        public int SourceInsertionIndex { get; init; }
        public bool CloseTargetWindowAfterUndo { get; init; }
    }
}
