using System.Windows;
using System.Windows.Threading;
using TabBrowser.Models;
using TabBrowser.Models.Workspaces;
using TabBrowser.Services;
using TabBrowser.Services.Workspaces;
using TabBrowser.ViewModels;

namespace TabBrowser.Views;

internal interface IWorkspaceNoteEditHost
{
    Dispatcher UiDispatcher { get; }

    Window DefaultOwner { get; }

    bool IsPrivateWindow { get; }

    string NoteDataScope { get; }

    bool IsClosing { get; }

    CancellationToken LifetimeToken { get; }

    void RecordRecentItem(RecentItem item);

    void SetStatus(string message);
}

internal interface IWorkspaceNoteStore
{
    Task<IReadOnlyList<WorkspaceItem>> GetWorkspaceItemsAsync(Guid workspaceId);

    Task UpdateNoteAsync(Guid noteId, string title, string content, double fontSize);

    Task<NoteItem> AddNoteAsync(Guid workspaceId, string title, string content, double fontSize);

    Task<IReadOnlyList<Workspace>> GetWorkspacesAsync();
}

internal sealed class WorkspaceNoteStore(WorkspaceService service) : IWorkspaceNoteStore
{
    public Task<IReadOnlyList<WorkspaceItem>> GetWorkspaceItemsAsync(Guid workspaceId)
        => service.GetWorkspaceItemsAsync(workspaceId);

    public Task UpdateNoteAsync(Guid noteId, string title, string content, double fontSize)
        => service.UpdateNoteAsync(noteId, title, content, fontSize);

    public Task<NoteItem> AddNoteAsync(Guid workspaceId, string title, string content, double fontSize)
        => service.AddNoteAsync(workspaceId, title, content, 32, 32, fontSize: fontSize);

    public Task<IReadOnlyList<Workspace>> GetWorkspacesAsync()
        => service.GetWorkspacesAsync();
}

public sealed class WorkspaceNoteEditController
{
    private readonly record struct EditorKey(string DataScope, bool IsPrivate, Guid NoteId);

    private sealed class EditorReservation
    {
        public TaskCompletionSource Prepared { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public WeakReference<WorkspaceNoteEditWindow>? Window { get; set; }
        public bool IsPreparing { get; set; } = true;
    }

    private static readonly object ReservationGate = new();
    private static readonly Dictionary<EditorKey, EditorReservation> Reservations = [];

    private readonly IWorkspaceNoteEditHost _host;
    private readonly Func<IWorkspaceNoteStore> _createStore;
    private readonly Func<string, string, Guid, double, WorkspaceNoteEditWindow> _createWindow;
    private readonly Func<WorkspaceNoteEditWindow, Task<bool>> _showWindowAsync;
    private readonly Action<WorkspaceNoteEditWindow, Guid> _saveWindowPlacement;
    private readonly Dictionary<Guid, WorkspaceNoteEditWindow> _windows = [];

    internal WorkspaceNoteEditController(
        IWorkspaceNoteEditHost host,
        Func<IWorkspaceNoteStore>? createStore = null,
        Func<string, string, Guid, double, WorkspaceNoteEditWindow>? createWindow = null,
        Func<WorkspaceNoteEditWindow, Task<bool>>? showWindowAsync = null,
        Action<WorkspaceNoteEditWindow, Guid>? saveWindowPlacement = null)
    {
        _host = host;
        _createStore = createStore ?? (() => new WorkspaceNoteStore(new WorkspaceService()));
        _createWindow = createWindow
            ?? ((title, content, noteId, fontSize) =>
                WorkspaceCanvasView.CreateWorkspaceNoteEditWindow(title, content, noteId, fontSize));
        _showWindowAsync = showWindowAsync ?? (window => ModelessWindowService.ShowAsync(window, allowMultiple: true));
        _saveWindowPlacement = saveWindowPlacement
            ?? ((window, noteId) => WorkspaceCanvasView.SaveWorkspaceNoteEditorWindowPlacement(window, noteId));
    }

    internal int OpenWindowCount => _windows.Count;

    internal static int ReservationCount
    {
        get { lock (ReservationGate) return Reservations.Count; }
    }

    public async Task EditAsync(Guid workspaceId, Guid noteId, Window? owner)
    {
        if (!_host.UiDispatcher.CheckAccess())
        {
            await _host.UiDispatcher.InvokeAsync(() => EditAsync(workspaceId, noteId, owner)).Task.Unwrap();
            return;
        }

        var key = new EditorKey(_host.NoteDataScope, _host.IsPrivateWindow, noteId);
        while (IsRequestActive(owner))
        {
            var (reservation, isCreator) = Reserve(key);
            if (isCreator)
            {
                await OpenReservedAsync(key, reservation, workspaceId, noteId, owner);
                return;
            }

            var (isPreparing, existingWindow) = GetPreparedWindow(reservation);
            if (isPreparing)
            {
                if (!await WaitForPreparedOrRequestEndAsync(reservation, owner)) return;
                continue;
            }

            if (!IsRequestActive(owner)) return;
            if (existingWindow is not null && await TryActivateAsync(existingWindow))
                return;

            Release(key, reservation);
        }
    }

    private async Task<bool> WaitForPreparedOrRequestEndAsync(EditorReservation reservation, Window? owner)
    {
        var requestEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler closedHandler = (_, _) => requestEnded.TrySetResult();
        _host.DefaultOwner.Closed += closedHandler;
        if (owner is not null && !ReferenceEquals(owner, _host.DefaultOwner))
            owner.Closed += closedHandler;
        using var lifetimeRegistration = _host.LifetimeToken.Register(() => requestEnded.TrySetResult());
        try
        {
            if (!IsRequestActive(owner)) return false;
            await Task.WhenAny(reservation.Prepared.Task, requestEnded.Task);
            return IsRequestActive(owner);
        }
        finally
        {
            _host.DefaultOwner.Closed -= closedHandler;
            if (owner is not null && !ReferenceEquals(owner, _host.DefaultOwner))
                owner.Closed -= closedHandler;
        }
    }

    private async Task OpenReservedAsync(EditorKey key, EditorReservation reservation,
        Guid workspaceId, Guid noteId, Window? owner)
    {
        WorkspaceNoteEditWindow? dialog = null;
        RoutedEventHandler? loadedHandler = null;
        EventHandler? closedHandler = null;
        EventHandler? ownerClosedHandler = null;
        CancellationTokenRegistration lifetimeRegistration = default;
        try
        {
            var ownerClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ownerClosedHandler = (_, _) => ownerClosed.TrySetResult();
            _host.DefaultOwner.Closed += ownerClosedHandler;
            if (owner is not null && !ReferenceEquals(owner, _host.DefaultOwner))
                owner.Closed += ownerClosedHandler;
            lifetimeRegistration = _host.LifetimeToken.Register(() => ownerClosed.TrySetResult());
            var store = _createStore();
            IReadOnlyList<WorkspaceItem> items;
            try
            {
                var load = store.GetWorkspaceItemsAsync(workspaceId);
                if (await Task.WhenAny(load, ownerClosed.Task) != load)
                {
                    _ = load.ContinueWith(static completed => _ = completed.Exception,
                        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted,
                        TaskScheduler.Default);
                    return;
                }
                items = await load;
            }
            catch (Exception ex)
            {
                _host.SetStatus("メモを読み込めません: " + ex.Message);
                return;
            }

            if (!IsRequestActive(owner)) return;

            if (items.FirstOrDefault(item => item.Id == noteId) is not NoteItem note)
            {
                _host.SetStatus("メモが見つかりません");
                return;
            }

            dialog = _createWindow(
                new WorkspaceItemViewModel(note).NoteEditorTitle,
                note.Content,
                note.Id,
                note.FontSize);
            if (!IsRequestActive(owner)) return;
            async Task<bool> SaveContentAsync(string content, double fontSize)
            {
                var updated = await UpdateContentAsync(
                    store,
                    note,
                    workspaceId,
                    content,
                    fontSize,
                    dialog.ReportMissingTarget);
                if (updated && dialog.IsLoaded)
                {
                    dialog.Title = new WorkspaceItemViewModel(note).NoteEditorTitle;
                }

                return updated;
            }

            dialog.SaveAndCloseAsync = SaveContentAsync;
            dialog.UpdateWithoutClosingAsync = SaveContentAsync;
            dialog.SaveAsNewAsync = async (content, fontSize) =>
            {
                // 親フォルダーも消えている可能性があるため、明示した元Workspace直下へ新規作成する。
                try
                {
                    await store.AddNoteAsync(workspaceId, WorkspaceOSViewModel.CreateInternalNoteTitle(content),
                        content, WorkspaceNoteFontSizeProfile.Normalize(fontSize));
                }
                catch (Exception ex)
                {
                    _host.SetStatus("新しいメモを保存できません: " + ex.Message);
                    return false;
                }

                _host.SetStatus("元のWorkspace直下に新しいメモを保存しました");
                return true;
            };
            dialog.Owner = owner is { IsVisible: true } ? owner : _host.DefaultOwner;
            _windows[noteId] = dialog;
            loadedHandler = (_, _) => MarkPrepared(key, reservation, dialog);
            closedHandler = (_, _) => Release(key, reservation);
            dialog.Loaded += loadedHandler;
            dialog.Closed += closedHandler;
            var showCompletion = _showWindowAsync(dialog);
            if (dialog.IsLoaded) MarkPrepared(key, reservation, dialog);
            await showCompletion;
        }
        finally
        {
            lifetimeRegistration.Dispose();
            if (ownerClosedHandler is not null)
            {
                _host.DefaultOwner.Closed -= ownerClosedHandler;
                if (owner is not null && !ReferenceEquals(owner, _host.DefaultOwner))
                    owner.Closed -= ownerClosedHandler;
            }
            if (dialog is not null && loadedHandler is not null) dialog.Loaded -= loadedHandler;
            if (dialog is not null && closedHandler is not null) dialog.Closed -= closedHandler;
            Release(key, reservation);
            if (dialog is not null)
            {
                try
                {
                    _saveWindowPlacement(dialog, noteId);
                }
                catch (Exception ex)
                {
                    _host.SetStatus("メモ編集Windowの位置を保存できません: " + ex.Message);
                }

                if (_windows.TryGetValue(noteId, out var registered)
                    && ReferenceEquals(registered, dialog))
                {
                    _windows.Remove(noteId);
                }
            }
        }
    }

    private bool IsRequestActive(Window? owner)
    {
        if (_host.IsClosing || _host.LifetimeToken.IsCancellationRequested
            || _host.UiDispatcher.HasShutdownStarted || _host.UiDispatcher.HasShutdownFinished
            || !_host.DefaultOwner.IsVisible)
            return false;
        return owner is null || owner.Dispatcher.CheckAccess() && owner.IsVisible;
    }

    private static (EditorReservation Reservation, bool IsCreator) Reserve(EditorKey key)
    {
        lock (ReservationGate)
        {
            if (Reservations.TryGetValue(key, out var existing)) return (existing, false);
            var created = new EditorReservation();
            Reservations.Add(key, created);
            return (created, true);
        }
    }

    private static (bool IsPreparing, WorkspaceNoteEditWindow? Window) GetPreparedWindow(EditorReservation reservation)
    {
        lock (ReservationGate)
        {
            WorkspaceNoteEditWindow? window = null;
            reservation.Window?.TryGetTarget(out window);
            return (reservation.IsPreparing, window);
        }
    }

    private static void MarkPrepared(EditorKey key, EditorReservation reservation, WorkspaceNoteEditWindow window)
    {
        lock (ReservationGate)
        {
            if (!Reservations.TryGetValue(key, out var current) || !ReferenceEquals(current, reservation)) return;
            reservation.Window = new WeakReference<WorkspaceNoteEditWindow>(window);
            reservation.IsPreparing = false;
        }
        reservation.Prepared.TrySetResult();
    }

    private static void Release(EditorKey key, EditorReservation reservation)
    {
        lock (ReservationGate)
        {
            if (Reservations.TryGetValue(key, out var current) && ReferenceEquals(current, reservation))
                Reservations.Remove(key);
        }
        reservation.Prepared.TrySetResult();
    }

    private static async Task<bool> TryActivateAsync(WorkspaceNoteEditWindow window)
    {
        if (window.Dispatcher.HasShutdownStarted || window.Dispatcher.HasShutdownFinished) return false;
        try
        {
            return await window.Dispatcher.InvokeAsync(() =>
            {
                if (!window.IsLoaded) return false;
                if (!window.IsVisible) window.Show();
                if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                window.Activate();
                window.FocusContentAtEnd();
                return true;
            }).Task;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task<bool> UpdateContentAsync(
        IWorkspaceNoteStore store,
        NoteItem note,
        Guid workspaceId,
        string content,
        double fontSize,
        Action targetMissing)
    {
        var title = WorkspaceOSViewModel.CreateInternalNoteTitle(content);
        var normalizedFontSize = WorkspaceNoteFontSizeProfile.Normalize(fontSize);
        try
        {
            await store.UpdateNoteAsync(note.Id, title, content, normalizedFontSize);
        }
        catch (WorkspaceNoteMissingException ex)
        {
            targetMissing();
            _host.SetStatus(ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            _host.SetStatus("メモを更新できません: " + ex.Message);
            return false;
        }

        note.Title = title;
        note.Content = content;
        note.FontSize = normalizedFontSize;
        _host.SetStatus("メモを更新しました");
        if (!_host.IsPrivateWindow)
        {
            try
            {
                var workspaceName = (await store.GetWorkspacesAsync())
                    .FirstOrDefault(workspace => workspace.Id == workspaceId)?.Name ?? "Workspace";
                _host.RecordRecentItem(RecentItemFactory.FromWorkspaceItem(note, workspaceName));
            }
            catch (Exception ex)
            {
                _host.SetStatus("メモは保存しましたが、最近使った項目を更新できません: " + ex.Message);
            }
        }

        return true;
    }
}
