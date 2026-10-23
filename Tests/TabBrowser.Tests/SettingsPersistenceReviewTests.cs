using TabBrowser.Models;
using TabBrowser.Services;
using Xunit;

namespace TabBrowser.Tests;

public sealed class SettingsPersistenceReviewTests
{
    [Fact]
    public void MissingPrimaryIsRecreatedWithUnchangedSettingsAsWellAsNewEdits()
    {
        using var files = new Files();
        files.Save(new AppSettings { SearchEngine = "kept" });
        var settings = files.Load();
        File.Delete(files.Path);
        settings.EnableLinkHoverPreview = false;
        files.Save(settings);
        Assert.Equal("kept", files.Load().SearchEngine);
        Assert.False(files.Load().EnableLinkHoverPreview);
    }

    [Fact]
    public void DialogResultMergesIntoLatestAndAcknowledgedBroadcastDoesNotBecomeAnotherEdit()
    {
        using var files = new Files();
        var a = files.Load();
        var b = files.Load();
        var beforeDialog = SettingsService.CloneForEdit(b);
        var result = SettingsService.CloneForEdit(b);
        a.SearchEngine = "other-window";
        files.Save(a);
        result.EnableLinkHoverPreview = false;
        var merged = SettingsService.MergeEditingResult(beforeDialog, result, files.Path);
        files.Save(merged);
        Assert.Equal("other-window", merged.SearchEngine);
        var beforeBroadcast = SettingsService.CloneForEdit(b);
        b.SearchEngine = merged.SearchEngine;
        b.EnableLinkHoverPreview = merged.EnableLinkHoverPreview;
        SettingsService.AcknowledgeSynchronizedChanges(b, beforeBroadcast);
        a.SearchEngine = "newer-other-window";
        files.Save(a);
        files.Save(b);
        Assert.Equal("newer-other-window", files.Load().SearchEngine);
        Assert.False(files.Load().EnableLinkHoverPreview);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DifferentWindowsPreserveUnrelatedEditsInEitherOrder(bool reverse)
    {
        using var files = new Files();
        var a = files.Load();
        var b = files.Load();
        a.SitePrivacy = SitePrivacySettingsProfile.RecordPopupPolicy(a.SitePrivacy, "https://example.test/", ContainerProfile.DefaultId, PopupPermissionPolicy.Allow, DateTimeOffset.UtcNow);
        b.EnableLinkHoverPreview = !b.EnableLinkHoverPreview;
        if (reverse) { files.Save(b); files.Save(a); }
        else { files.Save(a); files.Save(b); }
        var reopened = files.Load();
        Assert.Equal(b.EnableLinkHoverPreview, reopened.EnableLinkHoverPreview);
        Assert.Equal(PopupPermissionPolicy.Allow, Assert.Single(reopened.SitePrivacy.PopupRules).Policy);
        // 変更のない古いWindowを通常終了しても巻き戻さない。
        SettingsService.CaptureSaveOperation(b, files.Path)();
        Assert.Single(files.Load().SitePrivacy.PopupRules);
    }

    [Fact]
    public async Task ConcurrentAndOutOfOrderCapturedSavesKeepNewestChanges()
    {
        using var files = new Files();
        var a = files.Load();
        var b = files.Load();
        a.SearchEngine = "first";
        var oldSave = SettingsService.CaptureSaveOperation(a, files.Path);
        a.SearchEngine = "latest";
        var newSave = SettingsService.CaptureSaveOperation(a, files.Path);
        b.EnableLinkHoverPreview = false;
        await Task.WhenAll(Task.Run(newSave), SettingsService.SaveToFileAsync(files.Path, b));
        oldSave();
        Assert.Equal("latest", files.Load().SearchEngine);
        Assert.False(files.Load().EnableLinkHoverPreview);
    }

    [Fact]
    public void TemporarilyLockedPrimaryIsNeverRepairedFromOlderBackup()
    {
        using var files = new Files();
        files.Save(new AppSettings { SearchEngine = "old" });
        files.Save(new AppSettings { SearchEngine = "latest" });
        var primary = File.ReadAllBytes(files.Path);
        var backup = File.ReadAllBytes(AtomicFileService.CreateBackupFilePath(files.Path));
        AppSettings provisional;
        using (var locked = new FileStream(files.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            provisional = files.Load();
            Assert.Equal("old", provisional.SearchEngine);
            provisional.EnableLinkHoverPreview = false;
            Assert.ThrowsAny<IOException>(() => files.Save(provisional));
        }
        Assert.Equal(primary, File.ReadAllBytes(files.Path));
        Assert.Equal(backup, File.ReadAllBytes(AtomicFileService.CreateBackupFilePath(files.Path)));
        files.Save(provisional);
        Assert.Equal("latest", files.Load().SearchEngine);
        Assert.False(files.Load().EnableLinkHoverPreview);
    }

    [Fact]
    public void CorruptJsonStillRecoversBackupAndMissingFileStillCreatesDefaults()
    {
        using var files = new Files();
        files.Save(new AppSettings { SearchEngine = "kept" });
        files.Save(new AppSettings { SearchEngine = "newer" });
        File.WriteAllText(files.Path, "{invalid");
        Assert.Equal("kept", files.Load().SearchEngine);
        Assert.Equal("kept", files.Load().SearchEngine);
    }

    private sealed class Files : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Kiten-settings-review-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(_root, "settings.json");
        public Files() { Directory.CreateDirectory(_root); Load(); }
        public AppSettings Load() => new SettingsService(Path).Load();
        public void Save(AppSettings settings) => SettingsService.SaveToFile(Path, settings);
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
