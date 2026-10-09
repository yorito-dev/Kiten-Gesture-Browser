using Microsoft.Web.WebView2.Core;
using TabBrowser.Models;
using TabBrowser.Services;
using Xunit;

namespace TabBrowser.Tests;

public sealed class ContainerProfileStoreTests
{
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

        var profiles = store.Load();

        var profile = Assert.Single(profiles);
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

        var profiles = store.Load();

        Assert.Equal(2, profiles.Count);
        Assert.Equal(ContainerProfile.DefaultId, profiles[0].Id);
        Assert.True(profiles[0].IsDefault);
        Assert.Equal(customId.ToString("N"), profiles[1].Id);
        Assert.Equal("作業用", profiles[1].Name);
        Assert.Equal("#22C55E", profiles[1].Color);
        Assert.False(profiles[1].IsDefault);
        Assert.False(Directory.Exists(directory.ContainerProfilesFolder));
    }

    [Fact]
    public void LoadBrokenStoreFallsBackToDefaultProfile()
    {
        using var directory = new TempDirectory();
        File.WriteAllText(directory.StoreFile, "{ broken");

        var profiles = new ContainerProfileStore(directory.StoreFile).Load();

        var profile = Assert.Single(profiles);
        Assert.Equal(ContainerProfile.DefaultId, profile.Id);
        Assert.True(profile.IsDefault);
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

        var profile = store.Load().Single(static profile => !profile.IsDefault);

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
