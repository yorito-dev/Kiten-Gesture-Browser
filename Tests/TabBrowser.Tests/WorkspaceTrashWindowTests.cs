using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using TabBrowser.Models.Workspaces;
using TabBrowser.Services.Workspaces;
using TabBrowser.Views;
using Xunit;

namespace TabBrowser.Tests;

public sealed class WorkspaceTrashWindowTests
{
    [Fact]
    public void MissingParentRequiresDestinationAndRestoreCanBeUsedFromTheWindow() => RunInSta(async () =>
    {
        using var db = new TempWorkspaceDatabase();
        var service = new WorkspaceService(db.Repository());
        var workspace = await service.CreateWorkspaceAsync("検証", 1);
        var folder = await service.AddFolderAsync(workspace.Id, "親", 0, 0);
        var note = await service.AddNoteAsync(workspace.Id, "対象", "保護本文", 0, 0, folder.Id);
        await service.DeleteItemAsync(note.Id);
        await service.DeleteItemAsync(folder.Id);
        var window = new WorkspaceTrashWindow(service);
        try
        {
            await window.ReloadAsync();
            window.SelectItem(note.Id);
            await window.RestoreSelectedAsync();
            Assert.Contains("復元先を選択", window.StatusText);
            Assert.Equal(2, (await service.GetDeletedItemsAsync()).Count);
            window.SelectDestination(workspace.Id, null);
            await window.RestoreSelectedAsync();
            Assert.Contains("復元しました", window.StatusText);
            var loaded = Assert.Single(await service.GetWorkspaceItemsAsync(workspace.Id));
            Assert.Equal(note.Id, loaded.Id);
            Assert.Equal("保護本文", loaded.Note);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void PermanentDeletionNeedsPreparationAndRejectsAChangedConfirmationTarget() => RunInSta(async () =>
    {
        using var db = new TempWorkspaceDatabase();
        var service = new WorkspaceService(db.Repository());
        var workspace = await service.CreateWorkspaceAsync("検証", 1);
        var group = await service.AddSessionGroupAsync(workspace.Id, "対象", [new SessionTab { Url = "https://example.test/" }], 0, 0);
        await service.DeleteItemAsync(group.Id);
        var window = new WorkspaceTrashWindow(service);
        try
        {
            await window.ReloadAsync();
            window.SelectItem(group.Id);
            await window.ConfirmPermanentDeleteAsync();
            Assert.Single(await service.GetDeletedItemsAsync());
            window.PreparePermanentDelete();
            await service.RestoreItemAsync(group.Id, workspace.Id, null);
            await window.ConfirmPermanentDeleteAsync();
            Assert.Contains("確認し直して", window.StatusText);
            Assert.Single(await service.GetWorkspaceItemsAsync(workspace.Id));
            await service.DeleteItemAsync(group.Id);
            await window.ReloadAsync();
            window.SelectItem(group.Id);
            window.PreparePermanentDelete();
            await window.ConfirmPermanentDeleteAsync();
            Assert.Contains("完全削除しました", window.StatusText);
            Assert.Empty(await service.GetDeletedItemsAsync());
            Assert.Equal(0L, await db.ScalarAsync("SELECT COUNT(*) FROM workspace_session_tabs;"));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void OldConfirmationCannotDeleteANoteRestoredEditedAndTrashedAgain() => RunInSta(async () =>
    {
        using var db = new TempWorkspaceDatabase();
        var service = new WorkspaceService(db.Repository());
        var workspace = await service.CreateWorkspaceAsync("検証", 1);
        var note = await service.AddNoteAsync(workspace.Id, "対象", "元本文", 0, 0);
        await service.DeleteItemAsync(note.Id);
        var window = new WorkspaceTrashWindow(service);
        try
        {
            await window.ReloadAsync();
            window.SelectItem(note.Id);
            window.PreparePermanentDelete();
            await service.RestoreItemAsync(note.Id, workspace.Id, null);
            await service.UpdateNoteAsync(note.Id, "再編集", "新しく保護する本文", 18);
            await service.DeleteItemAsync(note.Id);
            await window.ConfirmPermanentDeleteAsync();
            Assert.Contains("確認し直して", window.StatusText);
            var retained = Assert.Single(await db.Repository().GetDeletedItemsAsync());
            Assert.Equal(note.Id, retained.Id);
            Assert.Equal("新しく保護する本文", retained.Note);
        }
        finally { window.Close(); }
    });

    internal static void RunInSta(Func<Task> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); }
                catch (Exception ex) { failure = ex; }
                finally { dispatcher.InvokeShutdown(); }
            });
            Dispatcher.Run();
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "検証用Dispatcherが期限内に完了しませんでした。");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
