using TabBrowser.Models;
using TabBrowser.Services;
using Xunit;

namespace TabBrowser.Tests;

public sealed class SettingsDialogPersistenceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ProductionDialogCopiesKeepUneditedSettingsAcrossSaveAndShutdown(bool editGeneral, bool otherWindowEdit)
    {
        var root = Path.Combine(Path.GetTempPath(), "Kiten-dialog-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "settings.json");
        var noteId = Guid.NewGuid();
        try
        {
            var initial = new AppSettings
            {
                PageZoomPreferences = [new() { Origin = "https://zoom.example", ZoomFactor = 1.5 }],
                FolderTabSortMode = FolderTabSortMode.LastWriteTime,
                FolderTabSortDirection = FolderTabSortDirection.Descending,
                FolderTabSortPreferences = [new() { FolderPath = root, SortMode = FolderTabSortMode.Size, SortDirection = FolderTabSortDirection.Descending }],
                CommandPaletteUsage = [new() { ItemId = "command.test", UseCount = 7, LastUsedAt = DateTimeOffset.UtcNow }],
                WorkspaceNoteEditorWindowPlacements = new() { [noteId] = new() { Left = 40, Top = 50, Width = 800, Height = 600 } }
            };
            SettingsService.SaveToFile(path, initial);
            var windowSettings = new SettingsService(path).Load();
            var beforeDialog = SettingsService.CloneForEdit(windowSettings);
            // コンストラクターとTryBuildResultが実際に使う同じコピーを両方通す。
            var working = SettingsWindow.CloneSettings(beforeDialog);
            if (otherWindowEdit)
            {
                var other = new SettingsService(path).Load();
                other.PageZoomPreferences[0].ZoomFactor = 1.75;
                other.Downloads.HistoryRetentionLimit = 77;
                SettingsService.SaveToFile(path, other);
            }
            var result = SettingsWindow.CloneSettings(working);
            if (editGeneral) result.EnableLinkHoverPreview = false;
            var merged = SettingsService.MergeEditingResult(beforeDialog, result, path);
            SettingsService.SaveToFile(path, merged);
            // 通常終了の設定保存入口と同じ捕捉処理を実行し、その後に再読込する。
            SettingsService.CaptureSaveOperation(windowSettings, path)();
            var reopened = new SettingsService(path).Load();
            Assert.Equal(otherWindowEdit ? 1.75 : 1.5, Assert.Single(reopened.PageZoomPreferences).ZoomFactor);
            Assert.Equal(FolderTabSortMode.LastWriteTime, reopened.FolderTabSortMode);
            Assert.Equal(FolderTabSortDirection.Descending, reopened.FolderTabSortDirection);
            Assert.Equal(FolderTabSortMode.Size, Assert.Single(reopened.FolderTabSortPreferences).SortMode);
            Assert.Equal(7, Assert.Single(reopened.CommandPaletteUsage).UseCount);
            Assert.Equal(800, reopened.WorkspaceNoteEditorWindowPlacements[noteId].Width);
            Assert.Equal(!editGeneral, reopened.EnableLinkHoverPreview);
            if (otherWindowEdit) Assert.Equal(77, reopened.Downloads.HistoryRetentionLimit);
            Assert.Equal(1.5, Assert.Single(beforeDialog.PageZoomPreferences).ZoomFactor);
            working.PageZoomPreferences.Clear();
            Assert.Single(beforeDialog.PageZoomPreferences);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}