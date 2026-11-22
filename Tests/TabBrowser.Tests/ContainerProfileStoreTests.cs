using Microsoft.Web.WebView2.Core;
using TabBrowser.Models;
using TabBrowser.Services;
using Xunit;

namespace TabBrowser.Tests;

public sealed class ContainerProfileStoreTests
{
    [Theory]
    [InlineData(ContainerProfileLoadStatus.RecoveredFromBackup,
        "コンテナ設定のファイルを読めないため、バックアップの内容を表示しています。編集はできません。")]
    [InlineData(ContainerProfileLoadStatus.Failed,
        "コンテナ設定のファイルを読めないため、コンテナを編集できません。")]
    [InlineData(ContainerProfileLoadStatus.Loaded, "")]
    [InlineData(ContainerProfileLoadStatus.Missing, "")]
    public void SettingsShowsTheReasonOnlyWhenContainerProfilesCannotBeEdited(
        ContainerProfileLoadStatus status, string expectedMessage)
    {
        Assert.Equal(expectedMessage, SettingsWindow.GetContainerProfileLoadError(status));
    }

    [Fact]
    public void SettingsProtectionUsesCurrentContainerIdsOnEveryCheck()
    {
        const string profileId = "0123456789abcdef0123456789abcdef";
        IReadOnlyCollection<string> inUse = [];
        IReadOnlyCollection<string> GetInUse() => inUse;

        Assert.True(SettingsWindow.IsProtectedContainerProfile(ContainerProfile.DefaultId, GetInUse));
        Assert.False(SettingsWindow.IsProtectedContainerProfile(profileId, GetInUse));

        inUse = [profileId.ToUpperInvariant()];
        Assert.True(SettingsWindow.IsProtectedContainerProfile(profileId, GetInUse));

        inUse = [];
        Assert.False(SettingsWindow.IsProtectedContainerProfile(profileId, GetInUse));
    }

    [Fact]
    public void BrowserVersionLookupDoesNotCreateAWebViewEnvironment()
    {
        var creationCount = 0;
        using var service = new WebViewEnvironmentService(
            userDataFolderOverride: null,
            (_, _) =>
            {
                creationCount++;
                return Task.FromException<CoreWebView2Environment>(
                    new InvalidOperationException("このFactoryは呼び出されません"));
            });

        var versions = service.GetCreatedBrowserVersionStrings();

        Assert.Empty(versions);
        Assert.Equal(0, creationCount);
        Assert.False(service.HasCreatedEnvironment);
    }

    [Fact]
    public void LoadMissingStoreReturnsDefaultProfileWithoutCreatingWebViewFolders()
    {
        using var directory = new TempDirectory();
        var store = new ContainerProfileStore(directory.StoreFile);

        var loaded = store.Load();

        Assert.Equal(ContainerProfileLoadStatus.Missing, loaded.Status);
        var profile = Assert.Single(loaded.Profiles);
        Assert.Equal(ContainerProfile.DefaultId, profile.Id);
        Assert.Equal(ContainerProfile.DefaultName, profile.Name);
        Assert.True(profile.IsDefault);
        Assert.False(File.Exists(directory.StoreFile));
        Assert.False(Directory.Exists(directory.ContainerProfilesFolder));
    }

    [Fact]
    public void SaveAndLoadNormalizesDefaultAndCustomProfiles()
    {
        using var directory = new TempDirectory();
        var store = new ContainerProfileStore(directory.StoreFile);
        var customId = Guid.NewGuid();

        store.Save(
        [
            new ContainerProfile
            {
                Id = "custom-not-guid",
                Name = "重複既定",
                IsDefault = true
            },
            new ContainerProfile
            {
                Id = customId.ToString(),
                Name = "  作業用  ",
                Color = "  #22C55E  "
            }
        ]);

        var loaded = store.Load();
        var profiles = loaded.Profiles;

        Assert.Equal(ContainerProfileLoadStatus.Loaded, loaded.Status);
        Assert.Equal(2, profiles.Count);
        Assert.Equal(ContainerProfile.DefaultId, profiles[0].Id);
        Assert.True(profiles[0].IsDefault);
        Assert.Equal(customId.ToString("N"), profiles[1].Id);
        Assert.Equal("作業用", profiles[1].Name);
        Assert.Equal("#22C55E", profiles[1].Color);
        Assert.False(profiles[1].IsDefault);
        Assert.False(Directory.Exists(directory.ContainerProfilesFolder));
    }

    [Theory]
    [InlineData("{ broken")]
    [InlineData("null")]
    [InlineData("[null]")]
    public void InvalidPrimaryLoadsBackupAndPreservesIdsMissingFromIt(string invalidPrimary)
    {
        using var directory = new TempDirectory();
        var store = new ContainerProfileStore(directory.StoreFile);
        var saved = new ContainerProfile { Id = Guid.NewGuid().ToString("N"), Name = "作業用" };
        var newer = new ContainerProfile { Id = Guid.NewGuid().ToString("N"), Name = "追加分" };
        Assert.True(store.Save([saved]));
        Assert.True(store.Save([saved, newer]));
        var backupFile = AtomicFileService.CreateBackupFilePath(directory.StoreFile);
        var backupBytes = File.ReadAllBytes(backupFile);
        File.WriteAllText(directory.StoreFile, invalidPrimary);

        var loaded = store.Load();

        Assert.Equal(ContainerProfileLoadStatus.RecoveredFromBackup, loaded.Status);
        var backupProfile = Assert.Single(loaded.Profiles, profile => !profile.IsDefault);
        Assert.Equal(saved.Id, backupProfile.Id);
        Assert.Equal(saved.Name, backupProfile.Name);

        var knownTab = new BrowserTab { ContainerProfileId = saved.Id };
        MainWindow.ApplyLoadedContainerProfile(knownTab, loaded);
        Assert.Equal(saved.Name, knownTab.ContainerProfileName);

        var newerTab = new BrowserTab { ContainerProfileId = newer.Id };
        MainWindow.ApplyLoadedContainerProfile(newerTab, loaded);
        Assert.Equal(newer.Id, newerTab.ContainerProfileId);

        Assert.False(store.Save(loaded.Profiles));
        Assert.Equal(invalidPrimary, File.ReadAllText(directory.StoreFile));
        Assert.Equal(backupBytes, File.ReadAllBytes(backupFile));
    }

    [Fact]
    public void FailedLoadKeepsRestoredTabIdAndBlocksOverwritingEitherFile()
    {
        using var directory = new TempDirectory();
        var store = new ContainerProfileStore(directory.StoreFile);
        var backupFile = AtomicFileService.CreateBackupFilePath(directory.StoreFile);
        File.WriteAllText(directory.StoreFile, "{ broken primary");
        File.WriteAllText(backupFile, "{ broken backup");
        var savedId = Guid.NewGuid().ToString("N");
        var sessionTab = new SessionTabState { Url = "https://example.test/", ContainerProfileId = savedId };
        var tab = BrowserTabStateMapper.CreateBrowserTab(BrowserTabStateMapper.FromSessionTabState(sessionTab), sessionTab.Url);

        var loaded = store.Load();
        MainWindow.ApplyLoadedContainerProfile(tab, loaded);

        Assert.Equal(ContainerProfileLoadStatus.Failed, loaded.Status);
        Assert.Empty(loaded.Profiles);
        Assert.Equal(savedId, tab.ContainerProfileId);
        Assert.False(store.Save([ContainerProfile.CreateDefault()]));
        Assert.Equal("{ broken primary", File.ReadAllText(directory.StoreFile));
        Assert.Equal("{ broken backup", File.ReadAllText(backupFile));
    }

    [Fact]
    public void LockedPrimaryCannotBeTreatedAsMissingOrOverwritten()
    {
        using var directory = new TempDirectory();
        var store = new ContainerProfileStore(directory.StoreFile);
        var profile = new ContainerProfile { Id = Guid.NewGuid().ToString("N"), Name = "作業用" };
        Assert.True(store.Save([profile]));

        using (File.Open(directory.StoreFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(ContainerProfileLoadStatus.Failed, store.Load().Status);
            Assert.False(store.Save([ContainerProfile.CreateDefault()]));
        }

        var loaded = store.Load();
        Assert.Equal(ContainerProfileLoadStatus.Loaded, loaded.Status);
        Assert.Contains(loaded.Profiles, current => current.Id == profile.Id);
    }

    [Fact]
    public void SuccessfulPrimaryLoadFallsBackForADeletedProfileEvenIfBackupContainsIt()
    {
        using var directory = new TempDirectory();
        var store = new ContainerProfileStore(directory.StoreFile);
        var saved = new ContainerProfile { Id = Guid.NewGuid().ToString("N"), Name = "削除対象" };
        Assert.True(store.Save([saved]));
        Assert.True(store.Save([]));
        var tab = new BrowserTab { ContainerProfileId = saved.Id };

        var loaded = store.Load();
        MainWindow.ApplyLoadedContainerProfile(tab, loaded);

        Assert.Equal(ContainerProfileLoadStatus.Loaded, loaded.Status);
        Assert.Equal(ContainerProfile.DefaultId, tab.ContainerProfileId);
        Assert.Equal(ContainerProfile.DefaultName, tab.ContainerProfileName);
    }

    [Fact]
    public void SaveAndLoadFallsBackToDefaultColorForInvalidColor()
    {
        using var directory = new TempDirectory();
        var store = new ContainerProfileStore(directory.StoreFile);

        store.Save(
        [
            new ContainerProfile
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "作業用",
                Color = "not-a-color"
            }
        ]);

        var profile = store.Load().Profiles.Single(static profile => !profile.IsDefault);

        Assert.Equal(ContainerProfile.DefaultColor, profile.Color);
    }

    [Fact]
    public void AppPathsMapsDefaultAndCustomWebViewFoldersWithoutCreatingThem()
    {
        var customId = Guid.NewGuid().ToString("N");

        var defaultPath = AppPaths.GetContainerProfileWebView2Folder(ContainerProfile.DefaultId);
        var customPath = AppPaths.GetContainerProfileWebView2Folder(customId);

        Assert.Equal(AppPaths.WebView2UserDataFolder, defaultPath);
        Assert.EndsWith(Path.Combine("containers", customId, "WebView2"), customPath);
    }

    [Fact]
    public void WebViewEnvironmentServiceResolvesUserDataFolderWithoutCreatingEnvironment()
    {
        var customId = Guid.NewGuid().ToString("N");
        var service = new WebViewEnvironmentService();

        var defaultPath = WebViewEnvironmentService.GetUserDataFolder(null);
        var customPath = WebViewEnvironmentService.GetUserDataFolder(customId);

        Assert.Equal(AppPaths.WebView2UserDataFolder, defaultPath);
        Assert.EndsWith(Path.Combine("containers", customId, "WebView2"), customPath);
        Assert.False(service.HasCreatedEnvironment);
        Assert.False(service.HasCreatedEnvironmentFor(customId));
        Assert.Empty(service.GetCreatedContainerProfileIds());
    }

    [Fact]
    public void AppPathsFallsBackToDefaultForNonGuidCustomContainerId()
    {
        var path = AppPaths.GetContainerProfileWebView2Folder("..\\unsafe");

        Assert.Equal(AppPaths.WebView2UserDataFolder, path);
    }

    [Fact]
    public void NormalizeIdKeepsGuidAndFallsBackForUnsafeText()
    {
        var customId = Guid.NewGuid();

        Assert.Equal(customId.ToString("N"), ContainerProfile.NormalizeId(customId.ToString()));
        Assert.Equal(ContainerProfile.DefaultId, ContainerProfile.NormalizeId("..\\unsafe"));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Directory.CreateDirectory(DirectoryPath);
        }

        public string DirectoryPath { get; } = Path.Combine(
            Path.GetTempPath(),
            "TabBrowserContainerProfileStoreTests",
            Guid.NewGuid().ToString("N"));

        public string StoreFile => Path.Combine(DirectoryPath, "container-profiles.json");

        public string ContainerProfilesFolder => Path.Combine(DirectoryPath, "containers");

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }
}
