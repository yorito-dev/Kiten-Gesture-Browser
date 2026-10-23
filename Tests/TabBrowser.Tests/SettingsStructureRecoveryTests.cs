using TabBrowser.Models;
using TabBrowser.Services;
using Xunit;

namespace TabBrowser.Tests;

public sealed class SettingsStructureRecoveryTests
{
    [Theory]
    [InlineData("{\"sitePrivacy\":{\"popupRules\":[null]}}")]
    [InlineData("{\"pageZoomPreferences\":[null]}")]
    [InlineData("null")]
    [InlineData("{invalid")]
    [InlineData("{\"sitePrivacy\":{\"popupRules\":{}}}")]
    public void InvalidStructureRecoversHealthyBackupWithoutChangingBackupBytes(string invalid)
    {
        var root = Path.Combine(Path.GetTempPath(), "Kiten-structure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "settings.json");
        var backup = AtomicFileService.CreateBackupFilePath(path);
        try
        {
            SettingsService.SaveToFile(path, new AppSettings { SearchEngine = "healthy-backup" });
            SettingsService.SaveToFile(path, new AppSettings { SearchEngine = "later" });
            var bytes = File.ReadAllBytes(backup);
            File.WriteAllText(path, invalid);
            Assert.Equal("healthy-backup", new SettingsService(path).Load().SearchEngine);
            Assert.Equal("healthy-backup", new SettingsService(path).Load().SearchEngine);
            Assert.Equal(bytes, File.ReadAllBytes(backup));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ExistingToleratedNullSectionsAndIgnoredInvalidEntriesStillNormalize()
    {
        var root = Path.Combine(Path.GetTempPath(), "Kiten-tolerated-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "settings.json");
        try
        {
            File.WriteAllText(path, """{"sitePrivacy":null,"pageZoomPreferences":null,"searchAliases":[null],"folderTabSortPreferences":[null]}""");
            var s = new SettingsService(path).Load();
            Assert.Empty(s.SitePrivacy.PopupRules);
            Assert.Empty(s.PageZoomPreferences);
            Assert.Empty(s.SearchAliases);
            Assert.Empty(s.FolderTabSortPreferences);
        }
        finally { Directory.Delete(root, true); }
    }
}