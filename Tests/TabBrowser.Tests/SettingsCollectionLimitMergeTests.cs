using TabBrowser.Models;
using TabBrowser.Services;
using Xunit;

namespace TabBrowser.Tests;

public sealed class SettingsCollectionLimitMergeTests
{
    public static TheoryData<string, bool> KindsAndOrders => new()
    {
        { "popup", false }, { "popup", true }, { "zoom", false }, { "zoom", true },
        { "folder", false }, { "folder", true }, { "usage", false }, { "usage", true }
    };

    [Theory]
    [MemberData(nameof(KindsAndOrders))]
    public void LocalLimitDoesNotDeleteAnotherWindowsUpdatedEntry(string kind, bool reverse)
    {
        using var files = new Files();
        var initial = files.Load();
        var limit = Limit(kind);
        for (var i = 0; i < limit; i++) Record(initial, kind, "item" + i, i + 1);
        files.Save(initial);
        var a = files.Load(); var b = files.Load(); var stale = files.Load();
        Record(a, kind, "item0", 10000);
        Record(b, kind, "new", 10001);
        Assert.False(Entries(b, kind).ContainsKey("item0"));
        if (reverse) { files.Save(b); files.Save(a); }
        else { files.Save(a); files.Save(b); }
        var loaded = Entries(files.Load(), kind);
        Assert.Equal(limit, loaded.Count);
        Assert.Equal(10000, loaded["item0"]);
        Assert.Equal(10001, loaded["new"]);
        Assert.False(loaded.ContainsKey("item1"));
        files.Save(stale); files.Save(a); files.Save(b);
        Assert.Equal(loaded, Entries(files.Load(), kind));
    }

    [Theory]
    [MemberData(nameof(KindsAndOrders))]
    public void ExplicitDeletionAtLimitSurvivesOtherAdditionAndStaleShutdown(string kind, bool reverse)
    {
        using var files = Full(kind);
        var a = files.Load(); var b = files.Load(); var stale = files.Load();
        Remove(a, kind, "item0");
        Record(b, kind, "new", 10001);
        if (reverse) { files.Save(b); files.Save(a); }
        else { files.Save(a); files.Save(b); }
        files.Save(stale); files.Save(b);
        var loaded = Entries(files.Load(), kind);
        Assert.Equal(Limit(kind), loaded.Count);
        Assert.False(loaded.ContainsKey("item0"));
        Assert.Equal(10001, loaded["new"]);
    }

    [Theory]
    [InlineData("popup")]
    [InlineData("zoom")]
    [InlineData("folder")]
    [InlineData("usage")]
    public void SameKeyAndReversedCapturedSavesKeepExistingConflictPolicyAtLimit(string kind)
    {
        using var files = Full(kind);
        var a = files.Load(); var b = files.Load();
        Record(a, kind, "item0", 20000); Record(b, kind, "item0", 10000);
        files.Save(a); files.Save(b);
        Assert.Equal(10000, Entries(files.Load(), kind)["item0"]);
        a = files.Load();
        Record(a, kind, "new", 30000);
        var older = SettingsService.CaptureSaveOperation(a, files.FilePath);
        Record(a, kind, "next", 30001);
        var newer = SettingsService.CaptureSaveOperation(a, files.FilePath);
        var other = files.Load(); Record(other, kind, "item1", 40000); files.Save(other);
        newer(); older(); files.Save(a);
        var loaded = Entries(files.Load(), kind);
        Assert.Equal(Limit(kind), loaded.Count);
        Assert.Equal(40000, loaded["item1"]);
        Assert.Equal(30000, loaded["new"]);
        Assert.Equal(30001, loaded["next"]);
    }

    [Theory]
    [InlineData("popup")]
    [InlineData("zoom")]
    [InlineData("folder")]
    [InlineData("usage")]
    public void DialogCloneAndMergeKeepAutomaticRemovalProvenance(string kind)
    {
        using var files = Full(kind);
        var a = files.Load(); var b = files.Load();
        var before = SettingsService.CloneForEdit(b);
        Record(b, kind, "new", 10001);
        var edited = SettingsService.CloneForEdit(b);
        Record(a, kind, "item0", 10000); files.Save(a);
        var merged = SettingsService.MergeEditingResult(before, edited, files.FilePath);
        // 統合時に上限から落ちた項目を、さらに別Windowが更新しても消さない。
        var other = files.Load(); Record(other, kind, "item1", 20000); files.Save(other);
        files.Save(merged);
        var loaded = Entries(files.Load(), kind);
        Assert.Equal(Limit(kind), loaded.Count);
        Assert.Equal(10000, loaded["item0"]);
        Assert.Equal(10001, loaded["new"]);
        Assert.Equal(20000, loaded["item1"]);
        Assert.False(loaded.ContainsKey("item2"));
    }

    [Fact]
    public void ExplicitPopupRemovalOverridesEarlierAutomaticEviction()
    {
        using var files = Full("popup");
        var a = files.Load(); var b = files.Load();
        Record(a, "popup", "item0", 10000); files.Save(a);
        Record(b, "popup", "new", 10001);
        Remove(b, "popup", "item0"); files.Save(b);
        Assert.False(Entries(files.Load(), "popup").ContainsKey("item0"));
    }

    [Theory]
    [InlineData("popup")]
    [InlineData("zoom")]
    [InlineData("folder")]
    [InlineData("usage")]
    public void LocallyEvictedAdditionStillUsesSlotFreedByAnotherWindow(string kind)
    {
        using var files = Full(kind);
        var a = files.Load(); var b = files.Load();
        Remove(a, kind, "item1"); files.Save(a);
        Record(b, kind, "new", -1);
        Assert.False(Entries(b, kind).ContainsKey("new"));
        files.Save(b);
        var loaded = Entries(files.Load(), kind);
        Assert.Equal(Limit(kind), loaded.Count);
        Assert.Equal(-1, loaded["new"]);
        Assert.False(loaded.ContainsKey("item1"));
    }

    [Theory]
    [InlineData("popup")]
    [InlineData("zoom")]
    [InlineData("folder")]
    [InlineData("usage")]
    public void LocallyEvictedUpdateIsAppliedBeforeLatestLimit(string kind)
    {
        using var files = Full(kind);
        var a = files.Load(); var b = files.Load();
        Remove(a, kind, "item1"); files.Save(a);
        Record(b, kind, "item0", -1); Record(b, kind, "new", 10001);
        Assert.False(Entries(b, kind).ContainsKey("item0"));
        files.Save(b);
        var loaded = Entries(files.Load(), kind);
        Assert.Equal(Limit(kind), loaded.Count);
        Assert.Equal(-1, loaded["item0"]);
        Assert.Equal(10001, loaded["new"]);
        Assert.False(loaded.ContainsKey("item1"));
    }

    [Theory]
    [InlineData("popup")]
    [InlineData("zoom")]
    [InlineData("folder")]
    [InlineData("usage")]
    public void LaterCapturedSaveDoesNotReplayAlreadySavedEvictedMutation(string kind)
    {
        using var files = Full(kind);
        var a = files.Load(); var b = files.Load();
        Remove(a, kind, "item1"); Remove(a, kind, "item2"); files.Save(a);
        Record(b, kind, "item0", -1); Record(b, kind, "new", 10001);
        var older = SettingsService.CaptureSaveOperation(b, files.FilePath);
        Record(b, kind, "next", 10002);
        var newer = SettingsService.CaptureSaveOperation(b, files.FilePath);
        older();
        Assert.Equal(-1, Entries(files.Load(), kind)["item0"]);
        var other = files.Load(); Record(other, kind, "item0", 20000); files.Save(other);
        newer(); files.Save(b);
        var loaded = Entries(files.Load(), kind);
        Assert.Equal(Limit(kind), loaded.Count);
        Assert.Equal(20000, loaded["item0"]);
        Assert.Equal(10001, loaded["new"]);
        Assert.Equal(10002, loaded["next"]);
        Assert.False(loaded.ContainsKey("item1")); Assert.False(loaded.ContainsKey("item2"));
    }

    [Theory]
    [InlineData("popup")]
    [InlineData("zoom")]
    [InlineData("folder")]
    [InlineData("usage")]
    public void DialogAndFailedSaveKeepEvictedEditWithoutRevertingUnrelatedUpdate(string kind)
    {
        using var files = Full(kind);
        var a = files.Load(); var b = files.Load();
        Remove(a, kind, "item1"); files.Save(a);
        var before = SettingsService.CloneForEdit(b);
        Record(b, kind, "new", -1);
        var edited = SettingsService.CloneForEdit(b);
        var merged = SettingsService.MergeEditingResult(before, edited, files.FilePath);
        using (new FileStream(files.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<IOException>(() => files.Save(merged));
        var other = files.Load(); Record(other, kind, "item0", 20000); files.Save(other);
        files.Save(merged); files.Save(b);
        var loaded = Entries(files.Load(), kind);
        Assert.Equal(Limit(kind), loaded.Count);
        Assert.Equal(-1, loaded["new"]);
        Assert.Equal(20000, loaded["item0"]);
        Assert.False(loaded.ContainsKey("item1"));
    }

    [Theory]
    [MemberData(nameof(KindsAndOrders))]
    public void ExplicitRemovalAfterUnsavedRecordDoesNotReplayUpsert(string kind, bool existing)
    {
        using var files = Full(kind);
        var settings = files.Load();
        var id = existing ? "item0" : "new";
        Record(settings, kind, id, 10000);
        Remove(settings, kind, id);
        files.Save(settings); files.Save(settings);
        Assert.False(Entries(files.Load(), kind).ContainsKey(id));
    }

    private static Files Full(string kind)
    {
        var files = new Files(); var settings = files.Load();
        for (var i = 0; i < Limit(kind); i++) Record(settings, kind, "item" + i, i + 1);
        files.Save(settings);
        return files;
    }

    private static void Remove(AppSettings settings, string kind, string id)
    {
        switch (kind)
        {
            case "popup": settings.SitePrivacy = SitePrivacySettingsProfile.RemovePopupPolicy(settings.SitePrivacy, "https://" + id + ".example", ContainerProfile.DefaultId); break;
            case "zoom": settings.PageZoomPreferences.RemoveAll(entry => entry.Origin == "https://" + id + ".example"); break;
            case "folder": settings.FolderTabSortPreferences.RemoveAll(entry => entry.FolderPath == Path.Combine(Path.GetTempPath(), "Kiten-limit", id)); break;
            case "usage": settings.CommandPaletteUsage.RemoveAll(entry => entry.ItemId == id); break;
        }
    }

    private static int Limit(string kind) => kind switch
    {
        "popup" => PopupPermissionPolicy.MaximumRuleCount,
        "zoom" => PageZoomPreferenceSettingsProfile.MaximumEntryCount,
        "folder" => FolderTabSortPreferenceSettingsProfile.MaximumEntryCount,
        _ => CommandPaletteUsageSettingsProfile.MaximumEntryCount
    };

    private static void Record(AppSettings settings, string kind, string id, int seconds)
    {
        var time = DateTimeOffset.UnixEpoch.AddSeconds(seconds);
        switch (kind)
        {
            case "popup": settings.SitePrivacy = SitePrivacySettingsProfile.RecordPopupPolicy(settings.SitePrivacy, "https://" + id + ".example/a", ContainerProfile.DefaultId, PopupPermissionPolicy.Allow, time); break;
            case "zoom": settings.PageZoomPreferences = PageZoomPreferenceSettingsProfile.Record(settings.PageZoomPreferences, "https://" + id + ".example/a", ContainerProfile.DefaultId, 1.5, time); break;
            case "folder":
                var path = Path.Combine(Path.GetTempPath(), "Kiten-limit", id);
                var existing = FolderTabSortPreferenceSettingsProfile.Find(settings.FolderTabSortPreferences, path);
                var mode = existing is null ? (seconds >= 10000 ? FolderTabSortMode.Size : FolderTabSortMode.Type)
                    : existing.SortMode == FolderTabSortMode.Size ? FolderTabSortMode.Type : FolderTabSortMode.Size;
                Assert.True(FolderTabSortPreferenceSettingsProfile.Update(settings, path, mode, FolderTabSortDirection.Descending, () => time));
                break;
            case "usage": settings.CommandPaletteUsage = CommandPaletteUsageSettingsProfile.Record(settings.CommandPaletteUsage, id, time); break;
        }
    }

    private static Dictionary<string, long> Entries(AppSettings settings, string kind) => kind switch
    {
        "popup" => settings.SitePrivacy.PopupRules.ToDictionary(entry => new Uri(entry.Origin).Host.Split('.')[0], entry => entry.UpdatedAt.ToUnixTimeSeconds()),
        "zoom" => settings.PageZoomPreferences.ToDictionary(entry => new Uri(entry.Origin).Host.Split('.')[0], entry => entry.UpdatedAt.ToUnixTimeSeconds()),
        "folder" => settings.FolderTabSortPreferences.ToDictionary(entry => Path.GetFileName(entry.FolderPath), entry => entry.UpdatedAt.ToUnixTimeSeconds()),
        _ => settings.CommandPaletteUsage.ToDictionary(entry => entry.ItemId, entry => entry.LastUsedAt.ToUnixTimeSeconds())
    };

    private sealed class Files : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "Kiten-limit-merge-" + Guid.NewGuid().ToString("N"));
        public string FilePath => Path.Combine(_root, "settings.json");
        public AppSettings Load() => new SettingsService(FilePath).Load();
        public void Save(AppSettings settings) => SettingsService.SaveToFile(FilePath, settings);
        public void Dispose() => Directory.Delete(_root, true);
    }
}
