using System.Collections.ObjectModel;
using System.Windows;
using TabBrowser.Models;
using TabBrowser.Services;

namespace TabBrowser.Views;

internal interface IFolderTabFileDropTarget
{
    string CurrentPath { get; }
    bool CanAcceptTabHeaderFileDrop { get; }
    bool ShouldCancelSameLocationFileDrop(IReadOnlyList<string> paths, string? destinationPath = null);
    Task<bool> TryHandleTabHeaderFileDropAsync(IReadOnlyList<string> paths, bool move);
}

internal interface IFolderTabFileDropHost
{
    BrowserTab? ActiveTab { get; }
    bool TryGetFileDropPaths(IDataObject data, out string[] paths);
    BrowserTab? FindTabFromOriginalSource(object originalSource);
    IFolderTabFileDropTarget? GetFolderTarget(BrowserTab? tab);
    void SelectTab(BrowserTab tab);
    Task ActivateSelectedTabAsync();
    void ClearDropPreview();
    void ResetWorkspaceHover();
    void ResetTabDetachCandidate();
    void SetStatus(string message, bool autoClear);
    void ClearStatus();
}

// タブ見出しへのファイルdrop表示と受付を所有する。実ファイル操作とデータ検証は既存ownerへ接続する。
internal sealed class FolderTabFileDropController : IDisposable
{
    internal const int FolderTabFileDropHoverDelayMilliseconds = 600;
    private readonly ObservableCollection<BrowserTab> _tabs;
    private readonly OwnedDispatcherTimer _timer;
    private readonly IFolderTabFileDropHost _host;
    private BrowserTab? _hoverTarget;
    private bool _move;
    private bool _disposed;

    internal FolderTabFileDropController(ObservableCollection<BrowserTab> tabs, OwnedDispatcherTimer timer, IFolderTabFileDropHost host)
    {
        _tabs = tabs;
        _timer = timer;
        _host = host;
        _timer.Tick += HoverTimer_Tick;
    }

    internal bool TryHandleDragOver(DragEventArgs e)
    {
        if (_disposed || !_host.TryGetFileDropPaths(e.Data, out var paths)) return false;
        _host.ClearDropPreview();
        _host.ResetWorkspaceHover();
        _host.ResetTabDetachCandidate();
        var targetTab = _host.FindTabFromOriginalSource(e.OriginalSource);
        if (targetTab is { IsWorkspaceTab: true } && _tabs.Contains(targetTab))
        {
            BeginHover(targetTab);
            _host.SetStatus("Workspaceへ参照保存", autoClear: false);
            e.Effects = DragDropEffects.Link;
            e.Handled = true;
            return true;
        }
        if (targetTab is not { IsFolderTab: true }
            || _host.GetFolderTarget(targetTab) is not { } folderTabView
            || !_tabs.Contains(targetTab)
            || !folderTabView.CanAcceptTabHeaderFileDrop)
        {
            ResetHover();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return true;
        }
        if (folderTabView.ShouldCancelSameLocationFileDrop(paths))
        {
            ResetHover();
            _host.SetStatus("同じ場所へのドロップはキャンセルされます", autoClear: true);
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return true;
        }
        var move = e.KeyStates.HasFlag(DragDropKeyStates.ShiftKey);
        BeginHover(targetTab);
        if (_move != move) _move = move;
        var destinationPath = string.IsNullOrWhiteSpace(folderTabView.CurrentPath)
            ? targetTab.Url ?? string.Empty
            : folderTabView.CurrentPath;
        var destinationName = string.IsNullOrWhiteSpace(destinationPath) ? "フォルダー" : FolderTabService.GetDisplayName(destinationPath);
        _host.SetStatus($"{(move ? "移動" : "コピー")} → {destinationName}", autoClear: false);
        e.Effects = move ? DragDropEffects.Move : DragDropEffects.Copy;
        e.Handled = true;
        return true;
    }

    private void BeginHover(BrowserTab targetTab)
    {
        if (ReferenceEquals(_hoverTarget, targetTab)) return;
        ResetHover();
        _hoverTarget = targetTab;
        targetTab.IsFolderFileDropTarget = true;
        if (!ReferenceEquals(_host.ActiveTab, targetTab)) _timer.Start();
    }

    private async void HoverTimer_Tick(object? sender, EventArgs e) => await ActivateHoveredTabAsync();

    internal async Task ActivateHoveredTabAsync()
    {
        _timer.Stop();
        var targetTab = _hoverTarget;
        if (_disposed || targetTab is null || !_tabs.Contains(targetTab)
            || (!targetTab.IsWorkspaceTab
                && (targetTab is not { IsFolderTab: true } || _host.GetFolderTarget(targetTab) is null))
            || ReferenceEquals(_host.ActiveTab, targetTab)) return;
        _host.SelectTab(targetTab);
        await _host.ActivateSelectedTabAsync();
    }

    internal async Task<bool> TryHandleDropAsync(DragEventArgs e)
    {
        if (_disposed || !_host.TryGetFileDropPaths(e.Data, out var paths)) return false;
        var targetTab = _host.FindTabFromOriginalSource(e.OriginalSource);
        IFolderTabFileDropTarget? folderTabView;
        if (targetTab is { IsFolderTab: true }
            && _host.GetFolderTarget(targetTab) is { } directTargetView
            && _tabs.Contains(targetTab))
        {
            folderTabView = directTargetView;
        }
        else
        {
            targetTab = _hoverTarget;
            folderTabView = _host.GetFolderTarget(targetTab);
        }
        var move = e.KeyStates.HasFlag(DragDropKeyStates.ShiftKey);
        ResetHover();
        if (targetTab is not { IsFolderTab: true } || folderTabView is null
            || !_tabs.Contains(targetTab) || !folderTabView.CanAcceptTabHeaderFileDrop)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return true;
        }
        if (!ReferenceEquals(_host.ActiveTab, targetTab))
        {
            _host.SelectTab(targetTab);
            await _host.ActivateSelectedTabAsync();
        }
        // Window終了中にactivationが完了しても、新しいファイル操作は開始しない。
        if (_disposed)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return true;
        }
        var accepted = await folderTabView.TryHandleTabHeaderFileDropAsync(paths, move);
        e.Effects = accepted ? move ? DragDropEffects.Move : DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        return true;
    }

    internal void HeaderDragLeave(DragEventArgs e)
    {
        if (_disposed || !_host.TryGetFileDropPaths(e.Data, out _)) return;
        ResetHover();
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    internal void ResetHover()
    {
        _timer.Stop();
        var hadTarget = _hoverTarget is not null;
        if (_hoverTarget is { } tab) tab.IsFolderFileDropTarget = false;
        _hoverTarget = null;
        _move = false;
        if (!_disposed && hadTarget) _host.ClearStatus();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Tick -= HoverTimer_Tick;
        ResetHover();
    }
}
