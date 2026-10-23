using TabBrowser.Models;
using TabBrowser.Services;
using Xunit;

namespace TabBrowser.Tests;

public sealed class SettingsCollectionMergeTests
{
    public static TheoryData<string, bool> KindsAndOrders => new()
    {
        { "popup", false }, { "popup", true }, { "zoom", false }, { "zoom", true },
        { "folder", false }, { "folder", true }, { "usage", false }, { "usage", true }
    };

    [Theory]
    [MemberData(nameof(KindsAndOrders))]
    public void IndependentAddsUpdatesAndDeletesSurviveReloadAndStaleShutdown(string kind, bool reverse)
    {
        using var files = new Files();
        var a = files.Load();
        var b = files.Load();
        Put(a, kind, "x", 10);
        Put(b, kind, "y", 10);
        SaveBoth();
        Assert.Equal(2, Values(files.Load(), kind).Count);
        a = files.Load(); b = files.Load();
        Put(a, kind, "x", 20); Put(b, kind, "y", 30);
        SaveBoth();
        Assert.Equal(20, Values(files.Load(), kind)["x"]);
        Assert.Equal(30, Values(files.Load(), kind)["y"]);
        a = files.Load(); b = files.Load();
        Remove(a, kind, "x"); Put(b, kind, "y", 20);
        SaveBoth();
        Assert.Equal(new Dictionary<string, long> { ["y"] = 20 }, Values(files.Load(), kind));
        SettingsService.CaptureSaveOperation(a, files.Path)();
        SettingsService.CaptureSaveOperation(b, files.Path)();
        Assert.Single(Values(files.Load(), kind));
        Assert.False(Values(files.Load(), kind).ContainsKey("x"));
        void SaveBoth()
        {
            if (reverse) { files.Save(b); files.Save(a); }
            else { files.Save(a); files.Save(b); }
        }
    }

    [Theory]
    [InlineData("popup")]
    [InlineData("zoom")]
    [InlineData("folder")]
    [InlineData("usage")]
    public async Task SameKeyUsesLastExecutedSaveAndOlderCapturedSaveCannotRollback(string kind)
    {
        using var files = new Files();
        var initial = files.Load(); Put(initial, kind, "x", 10); files.Save(initial);
        var a = files.Load(); var b = files.Load();
        Put(a, kind, "x", 30); Put(b, kind, "x", 20);
        files.Save(a); files.Save(b);
        Assert.Equal(20, Values(files.Load(), kind)["x"]);
        a = files.Load();
        Put(a, kind, "x", 10);
        var older = SettingsService.CaptureSaveOperation(a, files.Path);
        Put(a, kind, "x", 30);
        var newer = SettingsService.CaptureSaveOperation(a, files.Path);
        await Task.Run(newer); await Task.Run(older);
        Assert.Equal(30, Values(files.Load(), kind)["x"]);
        Remove(b, kind, "x"); files.Save(b);
        Assert.Empty(Values(files.Load(), kind));
    }

    [Theory]
    [InlineData("popup")]
    [InlineData("zoom")]
    public void SameOriginInDifferentContainersRemainsIndependent(string kind)
    {
        using var files = new Files();
        var a = files.Load(); var b = files.Load();
        var first = Guid.NewGuid().ToString("N"); var second = Guid.NewGuid().ToString("N");
        if (kind == "popup")
        {
            a.SitePrivacy = SitePrivacySettingsProfile.RecordPopupPolicy(a.SitePrivacy, "https://X.EXAMPLE/a", first, PopupPermissionPolicy.Allow, DateTimeOffset.UtcNow);
            b.SitePrivacy = SitePrivacySettingsProfile.RecordPopupPolicy(b.SitePrivacy, "https://x.example/b", second, PopupPermissionPolicy.Block, DateTimeOffset.UtcNow);
        }
        else
        {
            a.PageZoomPreferences = PageZoomPreferenceSettingsProfile.Record(a.PageZoomPreferences, "https://X.EXAMPLE/a", first, 1.25, DateTimeOffset.UtcNow);
            b.PageZoomPreferences = PageZoomPreferenceSettingsProfile.Record(b.PageZoomPreferences, "https://x.example/b", second, 1.5, DateTimeOffset.UtcNow);
        }
        files.Save(a); files.Save(b);
        var loaded = files.Load();
        if (kind == "popup")
        {
            Assert.Equal(2, loaded.SitePrivacy.PopupRules.Count);
            Assert.Equal(PopupPermissionPolicy.Allow, SitePrivacySettingsProfile.GetPopupPolicy(loaded.SitePrivacy, "https://x.example", first));
            Assert.Equal(PopupPermissionPolicy.Block, SitePrivacySettingsProfile.GetPopupPolicy(loaded.SitePrivacy, "https://x.example", second));
        }
        else
        {
            Assert.Equal(2, loaded.PageZoomPreferences.Count);
            Assert.Equal(1.25, PageZoomPreferenceSettingsProfile.GetZoomFactor(loaded.PageZoomPreferences, "https://x.example", first));
            Assert.Equal(1.5, PageZoomPreferenceSettingsProfile.GetZoomFactor(loaded.PageZoomPreferences, "https://x.example", second));
        }
    }

    [Fact]
    public void OrderedSearchAliasesRetainExplicitDialogOrder()
    {
        using var files = new Files(); var a = files.Load(); var b = files.Load();
        a.SearchAliases = [new() { Alias = "z", SearchUrlTemplate = "https://z.example/?q={0}" }, new() { Alias = "a", SearchUrlTemplate = "https://a.example/?q={0}" }];
        b.EnableLinkHoverPreview = false;
        files.Save(a); files.Save(b);
        Assert.Equal(new[] { "z", "a" }, files.Load().SearchAliases.Select(x => x.Alias));
        Assert.False(files.Load().EnableLinkHoverPreview);
    }

    private static string Folder(string id) => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Kiten-merge-" + id);
    private static void Put(AppSettings s, string kind, string id, int value)
    {
        var time = DateTimeOffset.FromUnixTimeSeconds(value);
        switch (kind)
        {
            case "popup": s.SitePrivacy = SitePrivacySettingsProfile.RecordPopupPolicy(s.SitePrivacy, "https://" + id + ".example/path", ContainerProfile.DefaultId, value == 20 ? PopupPermissionPolicy.Block : PopupPermissionPolicy.Allow, time); break;
            case "zoom": s.PageZoomPreferences = PageZoomPreferenceSettingsProfile.Record(s.PageZoomPreferences, "https://" + id + ".example/path", ContainerProfile.DefaultId, 1 + value / 10d, time); break;
            case "folder": s.FolderTabSortPreferences = FolderTabSortPreferenceSettingsProfile.Record(s.FolderTabSortPreferences, Folder(id), value == 20 ? FolderTabSortMode.Size : FolderTabSortMode.Type, FolderTabSortDirection.Descending, time); break;
            case "usage": s.CommandPaletteUsage.RemoveAll(x => x.ItemId == id); s.CommandPaletteUsage.Add(new() { ItemId = id, UseCount = value, LastUsedAt = time }); break;
        }
    }
    private static void Remove(AppSettings s, string kind, string id)
    {
        switch (kind)
        {
            case "popup": s.SitePrivacy = SitePrivacySettingsProfile.RemovePopupPolicy(s.SitePrivacy, "https://" + id + ".example", ContainerProfile.DefaultId); break;
            case "zoom": s.PageZoomPreferences.RemoveAll(x => x.Origin == "https://" + id + ".example"); break;
            case "folder": s.FolderTabSortPreferences.RemoveAll(x => x.FolderPath == Folder(id)); break;
            case "usage": s.CommandPaletteUsage.RemoveAll(x => x.ItemId == id); break;
        }
    }
    private static Dictionary<string, long> Values(AppSettings s, string kind) => kind switch
    {
        "popup" => s.SitePrivacy.PopupRules.ToDictionary(x => new Uri(x.Origin).Host[..1], x => x.UpdatedAt.ToUnixTimeSeconds()),
        "zoom" => s.PageZoomPreferences.ToDictionary(x => new Uri(x.Origin).Host[..1], x => x.UpdatedAt.ToUnixTimeSeconds()),
        "folder" => s.FolderTabSortPreferences.ToDictionary(x => x.FolderPath[^1..], x => x.UpdatedAt.ToUnixTimeSeconds()),
        _ => s.CommandPaletteUsage.ToDictionary(x => x.ItemId, x => x.LastUsedAt.ToUnixTimeSeconds())
    };
    private sealed class Files : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Kiten-collections-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(_root, "settings.json");
        public Files() { Directory.CreateDirectory(_root); Load(); }
        public AppSettings Load() => new SettingsService(Path).Load();
        public void Save(AppSettings s) => SettingsService.SaveToFile(Path, s);
        public void Dispose() => Directory.Delete(_root, true);
    }
}