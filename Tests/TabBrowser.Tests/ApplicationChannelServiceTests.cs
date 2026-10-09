using TabBrowser.Services;
using Xunit;

namespace TabBrowser.Tests;

public sealed class ApplicationChannelServiceTests
{
    [Fact]
    public void StableIsDefaultAndDevelopmentRequiresExplicitArgument()
    {
        Assert.Equal(ApplicationChannel.Stable, ApplicationChannelService.Resolve([]));
        Assert.Equal(
            ApplicationChannel.Stable,
            ApplicationChannelService.Resolve(["TabBrowser.exe", "--channel", "stable"]));
        Assert.Equal(
            ApplicationChannel.Development,
            ApplicationChannelService.Resolve(["TabBrowser.exe", "--channel", "development"]));
        Assert.Equal(
            ApplicationChannel.Development,
            ApplicationChannelService.Resolve(["TabBrowser.exe", "--channel=dev"]));
    }

    [Fact]
    public void StableAndDevelopmentUseSeparateDataRootsAndMutexes()
    {
        const string localApplicationData = @"C:\Users\tester\AppData\Local";
        Assert.Equal(
            Path.Combine(localApplicationData, "TabBrowser"),
            AppPaths.GetRoot(localApplicationData, ApplicationChannel.Stable));
        Assert.Equal(
            Path.Combine(localApplicationData, "TabBrowser-Dev"),
            AppPaths.GetRoot(localApplicationData, ApplicationChannel.Development));
        Assert.NotEqual(
            SingleInstanceService.GetMutexName(ApplicationChannel.Stable),
            SingleInstanceService.GetMutexName(ApplicationChannel.Development));
    }

    [Fact]
    public void ExistingInstanceActivationMatchesOnlyTheSameExecutablePath()
    {
        Assert.True(SingleInstanceService.AreSameExecutablePath(
            @"C:\work\TabBrowser\publish\TabBrowser-Stable\TabBrowser.exe",
            @"c:\WORK\TabBrowser\publish\TabBrowser-Stable\TabBrowser.exe"));
        Assert.False(SingleInstanceService.AreSameExecutablePath(
            @"C:\work\TabBrowser\publish\TabBrowser-Stable\TabBrowser.exe",
            @"C:\work\TabBrowser\publish\TabBrowser-Portable\TabBrowser.exe"));
        Assert.False(SingleInstanceService.AreSameExecutablePath(null, @"C:\TabBrowser.exe"));
    }

    [Fact]
    public void DevelopmentChannelIsVisibleInWindowTitleAndRuntimeDiagnostics()
    {
        var root = FindRepositoryRoot();
        var mainWindow = File.ReadAllText(Path.Combine(root, "MainWindow.xaml.cs"));
        var diagnostics = File.ReadAllText(
            Path.Combine(root, "Services", "Diagnostics", "RuntimeDiagnosticService.cs"));

        Assert.Contains("ApplicationChannelService.WindowTitleSuffix", mainWindow);
        Assert.Contains("チャンネル: {ApplicationChannelService.DisplayName}", diagnostics);
        Assert.Contains("バージョン: {GetApplicationVersion()}", diagnostics);
    }

    [Fact]
    public void DevelopmentChannelHasVisibleMainWindowBadgeWithoutChangingStableAppearance()
    {
        var root = FindRepositoryRoot();
        var mainWindowXaml = File.ReadAllText(Path.Combine(root, "MainWindow.xaml"));
        var mainWindowCode = File.ReadAllText(Path.Combine(root, "MainWindow.xaml.cs"));

        Assert.Contains("x:Name=\"DevelopmentChannelBadge\"", mainWindowXaml);
        Assert.Contains("Text=\"開発版\"", mainWindowXaml);
        Assert.Contains("Visibility=\"Collapsed\"", mainWindowXaml);
        Assert.Contains(
            "ApplicationChannelService.Current == ApplicationChannel.Development",
            mainWindowCode);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TabBrowser.csproj")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("TabBrowser.csprojが見つかりません");
    }
}
