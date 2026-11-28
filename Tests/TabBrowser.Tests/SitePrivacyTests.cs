using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Web.WebView2.Core;
using TabBrowser.Models;
using TabBrowser.Services;
using Xunit;

namespace TabBrowser.Tests;

public sealed class SitePrivacyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CrossWindowCandidatesKeepOnlyTheCallersPrivacyModeInInputOrder(bool isPrivate)
    {
        var caller = CreateTestWindow(isPrivate);
        var peer = CreateTestWindow(isPrivate);
        var opposite = CreateTestWindow(!isPrivate);

        Assert.Equal([peer, caller], caller.GetSamePrivacyMainWindows([opposite, peer, caller]));
        Assert.Equal([opposite], opposite.GetSamePrivacyMainWindows([opposite, peer, caller]));
    }

    [Fact]
    public void CrossWindowListsAndCommandsUseTheSamePrivacyBoundary()
    {
        var root = FindRepositoryRoot();
        var main = File.ReadAllText(Path.Combine(root, "MainWindow.xaml.cs"));
        var palette = File.ReadAllText(Path.Combine(root, "MainWindow.CommandPalette.cs"));
        var miniPlayer = File.ReadAllText(Path.Combine(root, "MainWindow.YouTubeMiniPlayer.cs"));
        var persistence = File.ReadAllText(Path.Combine(root, "Services", "YouTubeMiniPlayer", "Persistence", "YouTubeMiniPlayerPersistenceController.cs"));
        var list = File.ReadAllText(Path.Combine(root, "Views", "YouTubeMiniPlayerListViewController.cs"));

        Assert.Matches(@"CaptureWorkspaceOpenTabs\(\)\s*\{[^}]*foreach \(var window in GetSamePrivacyMainWindows\(GetOpenMainWindows\(\)\)\)", main);
        Assert.Matches(@"GetCommandPaletteTabSources\(\)\s*\{\s*return GetSamePrivacyMainWindows\(GetOpenMainWindows\(\)\)", palette);
        Assert.Matches(@"GetCommandPaletteMiniPlayerSources\(\)\s*\{\s*return GetSamePrivacyMainWindows\(GetOpenMainWindows\(\)\)", palette);
        Assert.Matches(@"IYouTubeMiniPlayerListHost\.GetOwnerSources\(\)\s*=> GetSamePrivacyMainWindows\(GetYouTubeMiniPlayerOwnerWindows\(\)\)", miniPlayer);
        Assert.Matches(@"IYouTubeMiniPlayerListHost\.GetActivePlayer\(\)\s*=> GetSamePrivacyMainWindows\(GetYouTubeMiniPlayerOwnerWindows\(\)\)", miniPlayer);
        Assert.Matches(@"IYouTubeMiniPlayerCommandHost\.GetOwners\(\)\s*=> GetSamePrivacyMainWindows\(GetYouTubeMiniPlayerOwnerWindows\(\)\)", miniPlayer);
        Assert.Matches(@"IYouTubeMiniPlayerPersistenceHost\.GetWorkspaceOwners\(\)\s*=> GetSamePrivacyMainWindows\(GetYouTubeMiniPlayerOwnerWindows\(\)\)", miniPlayer);
        Assert.Matches(@"CaptureWorkspaceMediaOverlayShelfItems\(\)\s*\{\s*return _host\.GetWorkspaceOwners\(\)", persistence);
        Assert.Matches(@"CaptureWorkspaceMediaOverlaysAsync\(\)\s*\{\s*foreach \(var window in _host\.GetWorkspaceOwners\(\)\)", persistence);
        Assert.Contains("var windows = _host.GetOwnerSources();", list);
        Assert.Contains("SynchronizeYouTubeMiniPlayerListItems(windows);", list);
    }

    internal static MainWindow CreateTestWindow(bool isPrivate)
    {
        // WindowやWebView2を起動せず、実装と同じプライバシー判定だけを使う。
        var window = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        typeof(MainWindow).GetField("_isPrivateWindow", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(window, isPrivate);
        return window;
    }

    [Fact]
    public void PopupRulesAreNormalizedAndSeparatedByContainer()
    {
        var workContainer = Guid.NewGuid().ToString("N");
        var settings = SitePrivacySettingsProfile.RecordPopupPolicy(
            null,
            "https://Example.COM/path",
            ContainerProfile.DefaultId,
            PopupPermissionPolicy.Allow,
            new DateTimeOffset(2026, 7, 26, 1, 0, 0, TimeSpan.Zero));
        settings = SitePrivacySettingsProfile.RecordPopupPolicy(
            settings,
            "https://example.com/other",
            workContainer,
            PopupPermissionPolicy.Block,
            new DateTimeOffset(2026, 7, 26, 2, 0, 0, TimeSpan.Zero));

        Assert.Equal(
            PopupPermissionPolicy.Allow,
            SitePrivacySettingsProfile.GetPopupPolicy(
                settings,
                "https://example.com/new",
                ContainerProfile.DefaultId));
        Assert.Equal(
            PopupPermissionPolicy.Block,
            SitePrivacySettingsProfile.GetPopupPolicy(
                settings,
                "https://example.com/new",
                workContainer));
        Assert.Null(SitePrivacySettingsProfile.GetPopupPolicy(
            settings,
            "https://other.example/",
            ContainerProfile.DefaultId));

        settings = SitePrivacySettingsProfile.RemovePopupPolicy(
            settings,
            "https://example.com/",
            workContainer);
        Assert.Null(SitePrivacySettingsProfile.GetPopupPolicy(
            settings,
            "https://example.com/new",
            workContainer));
        Assert.Equal(
            PopupPermissionPolicy.Allow,
            SitePrivacySettingsProfile.GetPopupPolicy(
                settings,
                "https://example.com/new",
                ContainerProfile.DefaultId));
    }

    [Fact]
    public void PermissionAndBrowsingDataMappingsStayWithinSupportedScope()
    {
        Assert.True(SitePrivacyService.IsManagedPermission(CoreWebView2PermissionKind.Camera));
        Assert.True(SitePrivacyService.IsManagedPermission(CoreWebView2PermissionKind.Microphone));
        Assert.True(SitePrivacyService.IsManagedPermission(CoreWebView2PermissionKind.Notifications));
        Assert.True(SitePrivacyService.IsManagedPermission(CoreWebView2PermissionKind.Geolocation));
        Assert.False(SitePrivacyService.IsManagedPermission(CoreWebView2PermissionKind.ClipboardRead));

        var kinds = SitePrivacyService.GetBrowsingDataKinds(
            SiteDataCategory.Cache | SiteDataCategory.CookiesAndSiteData);
        Assert.True(kinds.HasFlag(CoreWebView2BrowsingDataKinds.DiskCache));
        Assert.True(kinds.HasFlag(CoreWebView2BrowsingDataKinds.Cookies));
        Assert.True(kinds.HasFlag(CoreWebView2BrowsingDataKinds.IndexedDb));
        Assert.False(kinds.HasFlag(CoreWebView2BrowsingDataKinds.BrowsingHistory));
    }

    [Fact]
    public void SettingsRoundTripKeepsPopupRules()
    {
        using var directory = new TempSettingsDirectory();
        var settings = new AppSettings
        {
            SitePrivacy = SitePrivacySettingsProfile.RecordPopupPolicy(
                null,
                "https://example.com/",
                ContainerProfile.DefaultId,
                PopupPermissionPolicy.Allow,
                DateTimeOffset.UtcNow)
        };

        SettingsService.SaveToFile(directory.SettingsFile, settings);
        var loaded = new SettingsService(directory.SettingsFile).Load();

        Assert.Equal(
            PopupPermissionPolicy.Allow,
            SitePrivacySettingsProfile.GetPopupPolicy(
                loaded.SitePrivacy,
                "https://example.com/page",
                ContainerProfile.DefaultId));
    }

    [Fact]
    public void SiteAddressInformationDescribesConnectionWithoutClaimingSafety()
    {
        Assert.True(SitePrivacyService.TryCreateAddressInformation(
            "https://Example.COM:443/path?q=1",
            out var https));
        Assert.Equal("https://example.com", https.Origin);
        Assert.Equal("example.com", https.Host, ignoreCase: true);
        Assert.Equal("HTTPSで接続", https.ConnectionName);
        Assert.True(https.SupportsSitePermissions);

        Assert.True(SitePrivacyService.TryCreateAddressInformation(
            "http://example.com/path",
            out var http));
        Assert.Equal("HTTPで接続（暗号化されていません）", http.ConnectionName);
        Assert.DoesNotContain("安全", https.ConnectionName);
        Assert.False(SitePrivacyService.TryCreateAddressInformation("javascript:alert(1)", out _));
    }

    [Theory]
    [InlineData("https://example.com/", false, AddressConnectionBadgeKind.None)]
    [InlineData("https://example.com/", true, AddressConnectionBadgeKind.CertificateError)]
    [InlineData("http://example.com/", false, AddressConnectionBadgeKind.Unprotected)]
    [InlineData("file:///C:/example.txt", false, AddressConnectionBadgeKind.File)]
    [InlineData("tabbrowser://newtab", false, AddressConnectionBadgeKind.None)]
    [InlineData("about:blank", false, AddressConnectionBadgeKind.None)]
    [InlineData("data:text/html,%3Cp%3Etest%3C/p%3E", false, AddressConnectionBadgeKind.None)]
    [InlineData("javascript:alert(1)", false, AddressConnectionBadgeKind.None)]
    [InlineData(null, false, AddressConnectionBadgeKind.None)]
    public void AddressConnectionBadgeDependsOnlyOnUrlAndCertificateError(
        string? url,
        bool hasCertificateError,
        AddressConnectionBadgeKind expected)
    {
        Assert.Equal(expected, SitePrivacyService.GetAddressConnectionBadgeKind(url, hasCertificateError));
    }

    [Theory]
    [InlineData(CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect, true)]
    [InlineData(CoreWebView2WebErrorStatus.CertificateExpired, true)]
    [InlineData(CoreWebView2WebErrorStatus.ClientCertificateContainsErrors, true)]
    [InlineData(CoreWebView2WebErrorStatus.CertificateRevoked, true)]
    [InlineData(CoreWebView2WebErrorStatus.CertificateIsInvalid, true)]
    [InlineData(CoreWebView2WebErrorStatus.Timeout, false)]
    public void OnlyCertificateNavigationFailuresSetTheCertificateBadge(
        CoreWebView2WebErrorStatus status,
        bool expected)
    {
        Assert.Equal(expected, SitePrivacyService.IsCertificateNavigationError(status));
    }

    [Theory]
    [InlineData("https://example.com/page", "https://example.com/page", true)]
    [InlineData("https://example.com/page#section", "https://example.com/page", true)]
    [InlineData("https://example.com/page", "https://example.com/image.png", false)]
    [InlineData("https://example.com/page", "https://other.example/page", false)]
    [InlineData("http://example.com/page", "http://example.com/page", false)]
    public void CertificateRequestMustBelongToTheCurrentPageNavigation(
        string navigationUrl,
        string requestUrl,
        bool expected)
    {
        Assert.Equal(expected, SitePrivacyService.IsCurrentPageCertificateRequest(navigationUrl, requestUrl));
    }

    [Fact]
    public void PageNavigationAndRedirectKeepTheDisplayedAndSavedUrlUntilCommit()
    {
        const string currentUrl = "https://current.test/";
        var tab = new BrowserTab { Url = currentUrl };
        Assert.True(tab.AddressNavigation.TryStart(1, "https://requested.test/", isRedirect: false));
        Assert.True(tab.AddressNavigation.TryStart(1, "https://redirected.test/", isRedirect: true));

        Assert.Equal(currentUrl, tab.AddressUrl);
        Assert.Equal(currentUrl, tab.Url);
        Assert.Equal(currentUrl, BrowserTabStateMapper.Capture(tab).Url);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AppNavigationShowsOnlyItsMatchedRequestThenRestoresTheCurrentUrlOnFailure(bool certificateFailure)
    {
        const string currentUrl = "https://current.test/";
        const string displayUrl = "https://requested.test/watch";
        const string actualRequest = displayUrl + "?autoplay=0";
        var tab = new BrowserTab { Url = currentUrl };
        var navigation = tab.AddressNavigation;
        navigation.ExpectAppRequest(actualRequest, displayUrl);
        Assert.True(navigation.HasPendingNavigation);
        Assert.True(navigation.TryStart(2, actualRequest, isRedirect: false));
        Assert.True(navigation.TryStart(2, "https://redirected.test/", isRedirect: true));
        Assert.False(navigation.TryStart(1, "https://previous-redirect.test/", isRedirect: true));

        Assert.Equal(displayUrl, tab.AddressUrl);
        Assert.True(navigation.HasPendingNavigation);
        Assert.Equal(currentUrl, BrowserTabStateMapper.Capture(tab).Url);
        Assert.False(navigation.TryComplete(1, certificateFailure));
        Assert.Equal(displayUrl, tab.AddressUrl);
        Assert.True(navigation.TryComplete(2, certificateFailure));
        Assert.False(navigation.HasPendingNavigation);
        Assert.Equal(currentUrl, tab.AddressUrl);
        Assert.Equal(currentUrl, tab.Url);
    }

    [Fact]
    public void UnmatchedPageRequestDoesNotUseAnAppAddressMarker()
    {
        const string requested = "https://example.test/Case";
        var tab = new BrowserTab { Url = "https://current.test/" };
        tab.AddressNavigation.ExpectAppRequest(requested, requested);
        Assert.True(tab.AddressNavigation.TryStart(1, "https://example.test/case", isRedirect: false));

        Assert.Equal(tab.Url, tab.AddressUrl);
        Assert.True(tab.AddressNavigation.TryStart(2, requested, isRedirect: false));
        Assert.Equal(tab.Url, tab.AddressUrl);
    }

    [Fact]
    public void CertificateBadgeSurvivesAbortAndSameDocumentChangesUntilTheNextDocumentCommits()
    {
        const string badCertificateUrl = "https://expired.test/";
        var navigation = new BrowserTab().AddressNavigation;
        Assert.True(navigation.TryStart(1, badCertificateUrl, isRedirect: false));
        Assert.True(navigation.TryComplete(1, certificateFailure: true));
        Assert.Equal(badCertificateUrl, navigation.CertificateErrorUrl);

        Assert.True(navigation.TryStart(2, "https://next.test/", isRedirect: false));
        Assert.Equal(badCertificateUrl, navigation.CertificateErrorUrl);
        Assert.True(navigation.TryComplete(2, certificateFailure: false));
        navigation.ObserveSameDocumentSource(badCertificateUrl + "#anchor");
        Assert.Equal(badCertificateUrl, navigation.CertificateErrorUrl);

        Assert.True(navigation.TryStart(3, "https://next.test/", isRedirect: false));
        Assert.False(navigation.RecordCertificateError("https://next.test/image.png"));
        Assert.Equal(badCertificateUrl, navigation.CertificateErrorUrl);
        Assert.True(navigation.TryCommit(3));
        Assert.Null(navigation.CertificateErrorUrl);
    }

    [Fact]
    public void NavigationEventsSeparateDisplayFromTheUrlUsedForSessionAndClosedTabs()
    {
        var root = FindRepositoryRoot();
        var main = File.ReadAllText(Path.Combine(root, "MainWindow.xaml.cs"));
        var start = main.IndexOf("navigationStarting = (_, args)", StringComparison.Ordinal);
        var complete = main.IndexOf("navigationCompleted = (_, args)", start, StringComparison.Ordinal);
        var startingHandler = main[start..complete];
        Assert.Contains("tab.AddressNavigation.TryStart(args.NavigationId, args.Uri, args.IsRedirected)", startingHandler);
        Assert.DoesNotContain("tab.Url =", startingHandler);
        Assert.Contains("if (_tabs.Contains(tab) && !args.IsErrorPage)", main);
        Assert.Contains("CommitTabNavigation(tab, args.NavigationId, core.Source);", main);
        Assert.Contains("tab.Url = tab.AddressNavigation.InternalDocumentUrl ?? source;", main);
        Assert.Contains("tab.AddressNavigation.TryComplete(args.NavigationId, hasCertificateNavigationError)", main);
        Assert.Contains("tab.AddressNavigation.ExpectAppRequest(navigationUrl, url);", main);
        Assert.Contains("? tab.Url?.Trim()", main);
        Assert.DoesNotContain("NormalizeSessionTabUrl(tab.AddressUrl", main);
        Assert.Contains("return !tab.AddressNavigation.HasPendingNavigation", main);
        var splitView = File.ReadAllText(Path.Combine(root, "Views", "SplitViewDisplayController.cs"));
        Assert.Contains("host.SetAddressText(host.IsNewTabUrl(activeTab.AddressUrl)", splitView);
    }

    [Fact]
    public void PermissionManagerCanResetAdditionalSavedPermissionsWithoutExpandingGrants()
    {
        var settings = new[]
        {
            ("https://example.test", CoreWebView2PermissionKind.Camera, CoreWebView2PermissionState.Allow),
            ("https://example.test", CoreWebView2PermissionKind.ClipboardRead, CoreWebView2PermissionState.Deny),
            ("https://example.test", CoreWebView2PermissionKind.UnknownPermission, CoreWebView2PermissionState.Allow),
            ("file:///tmp", CoreWebView2PermissionKind.LocalFonts, CoreWebView2PermissionState.Allow)
        };
        Assert.Equal(CoreWebView2PermissionKind.Camera,
            Assert.Single(SitePrivacyService.CreatePermissionEntries(settings, false)).PermissionKind);
        var managed = SitePrivacyService.CreatePermissionEntries(settings, true);
        Assert.Equal(2, managed.Count);
        var clipboard = Assert.Single(managed, entry => entry.PermissionKind == CoreWebView2PermissionKind.ClipboardRead);
        Assert.True(SitePrivacyService.CanChangePermission(clipboard.PermissionKind, CoreWebView2PermissionState.Default));
        Assert.False(SitePrivacyService.CanChangePermission(clipboard.PermissionKind, CoreWebView2PermissionState.Allow));
        Assert.False(SitePrivacyService.CanChangePermission(clipboard.PermissionKind, CoreWebView2PermissionState.Deny));
        Assert.False(SitePrivacyService.IsManagedPermission(clipboard.PermissionKind));
    }

    [Fact]
    public void SitePermissionEntriesAreFilteredToExactOrigin()
    {
        var entries = new[]
        {
            new SitePermissionEntry(
                "https://example.com",
                CoreWebView2PermissionKind.Camera,
                CoreWebView2PermissionState.Allow,
                "カメラ",
                "許可"),
            new SitePermissionEntry(
                "https://sub.example.com",
                CoreWebView2PermissionKind.Microphone,
                CoreWebView2PermissionState.Deny,
                "マイク",
                "拒否")
        };

        var filtered = SitePrivacyService.GetEntriesForOrigin(entries, "https://EXAMPLE.com/page");

        var entry = Assert.Single(filtered);
        Assert.Equal("カメラ", entry.PermissionName);
    }

    [Fact]
    public void MainWindowConnectsAndDisconnectsPrivacyLifecycleWithoutWhiteUi()
    {
        var root = FindRepositoryRoot();
        var appCode = File.ReadAllText(Path.Combine(root, "App.xaml.cs"));
        var mainWindow = File.ReadAllText(Path.Combine(root, "MainWindow.xaml.cs"));
        var privacyCode = File.ReadAllText(Path.Combine(root, "MainWindow.SitePrivacy.cs"));
        var mainXaml = File.ReadAllText(Path.Combine(root, "MainWindow.xaml"));
        var privacyXaml = File.ReadAllText(Path.Combine(root, "SitePrivacyWindow.xaml"));
        var promptXaml = File.ReadAllText(Path.Combine(root, "SitePermissionPromptWindow.xaml"));

        Assert.Contains("Core.PermissionRequested += PermissionRequested;", mainWindow);
        Assert.Contains("TryDetach(() => Core.PermissionRequested -= PermissionRequested);", mainWindow);
        Assert.Contains("Core.ScreenCaptureStarting += ScreenCaptureStarting;", mainWindow);
        Assert.Contains("TryDetach(() => Core.ScreenCaptureStarting -= ScreenCaptureStarting);", mainWindow);
        Assert.Contains("persistHistory: !_isPrivateWindow", mainWindow);
        Assert.Contains("if (!_isInitialized || _isPrivateWindow)", mainWindow);
        Assert.Contains("HandleAutomaticPopup(tab, args.Uri);", mainWindow);
        Assert.Contains("isPrivateWindow: true", privacyCode);
        Assert.Contains("サイト権限・プライバシー", mainXaml);
        Assert.Contains("x:Name=\"SiteInformationButton\"", mainXaml);
        Assert.Contains("x:Name=\"SiteInformationPopup\"", mainXaml);
        var addressAreaStart = mainXaml.IndexOf("<Border x:Name=\"AddressArea\"", StringComparison.Ordinal);
        var goButtonStart = mainXaml.IndexOf("<Button x:Name=\"GoButton\"", StringComparison.Ordinal);
        var extensionIconListStart = mainXaml.IndexOf("<ItemsControl x:Name=\"ExtensionIconList\"", StringComparison.Ordinal);
        var addressAreaEnd = mainXaml.IndexOf("<Grid>", addressAreaStart, StringComparison.Ordinal);
        Assert.True(addressAreaStart >= 0 && addressAreaEnd > addressAreaStart, "アドレス欄の外枠が見つかりません。");
        Assert.True(
            addressAreaStart > goButtonStart && addressAreaStart > extensionIconListStart,
            "ツールバーボタンを先に描画し、アドレス欄を最後に描画する必要があります。");
        var addressAreaMarkup = mainXaml[addressAreaStart..addressAreaEnd];
        Assert.Contains("MinWidth=\"220\"", addressAreaMarkup);
        Assert.Contains("MaxWidth=\"720\"", addressAreaMarkup);
        Assert.Contains("Margin=\"0,0,8,0\"", addressAreaMarkup);
        Assert.Contains("CornerRadius=\"7\"", addressAreaMarkup);
        Assert.Contains("Panel.ZIndex=\"1\"", addressAreaMarkup);
        Assert.Contains("Value=\"#1D2530\"", addressAreaMarkup);
        Assert.Contains("Value=\"#222E3D\"", addressAreaMarkup);
        Assert.Contains("Value=\"#18202A\"", mainXaml);
        Assert.Contains("x:Name=\"AddressAreaInnerHighlight\"", mainXaml);
        Assert.Contains("Background=\"#18FFFFFF\"", mainXaml);
        Assert.Contains("Panel.ZIndex=\"9\"", mainXaml);
        Assert.Contains("x:Name=\"AddressAreaOutline\"", mainXaml);
        Assert.Contains("Margin=\"0,0,0,1\"", mainXaml);
        Assert.Contains("HorizontalAlignment=\"Left\"", mainXaml);
        Assert.Contains("Panel.ZIndex=\"10\"", mainXaml);
        Assert.Contains("Property=\"BorderBrush\" Value=\"#687282\"", mainXaml);
        Assert.Contains("Property=\"BorderBrush\" Value=\"#8AB4F8\"", mainXaml);
        var addressBoxStart = mainXaml.IndexOf("<TextBox x:Name=\"AddressBox\"", StringComparison.Ordinal);
        var addressBoxEnd = mainXaml.IndexOf("<TextBox.ContextMenu>", addressBoxStart, StringComparison.Ordinal);
        Assert.True(addressBoxStart >= 0 && addressBoxEnd > addressBoxStart, "アドレス入力欄が見つかりません。");
        var addressBoxMarkup = mainXaml[addressBoxStart..addressBoxEnd];
        Assert.Contains("CaretBrush=\"#FFFFFF\"", addressBoxMarkup);
        Assert.Contains("SelectionBrush=\"#2563EB\"", addressBoxMarkup);
        Assert.Contains("SelectionOpacity=\"1\"", addressBoxMarkup);
        Assert.Contains("SelectionTextBrush=\"#FFFFFF\"", addressBoxMarkup);
        Assert.Contains("SystemColors.HighlightBrushKey", addressBoxMarkup);
        Assert.Contains("SystemColors.HighlightTextBrushKey", addressBoxMarkup);
        Assert.Contains("Color=\"#2563EB\"", addressBoxMarkup);
        Assert.Contains("Color=\"#FFFFFF\"", addressBoxMarkup);
        Assert.Matches(
            "Switch\\.System\\.Windows\\.Controls\\.Text\\.UseAdornerForTextboxSelectionRendering\"\\s*,\\s*false\\)",
            appCode);
        Assert.True(
            appCode.IndexOf("AppContext.SetSwitch", StringComparison.Ordinal)
                < appCode.IndexOf("_singleInstanceService.TryAcquirePrimaryInstance", StringComparison.Ordinal),
            "選択文字の描画方式はWPFコントロール生成前に設定する必要があります。");
        Assert.DoesNotContain("AddressAreaBottomLine", mainXaml);
        Assert.Contains("CornerRadius=\"7,0,0,7\"", mainXaml);
        Assert.Contains("x:Key=\"AddressInformationButtonStyle\"", mainXaml);
        Assert.Contains("Style=\"{StaticResource AddressInformationButtonStyle}\"", mainXaml);
        Assert.Contains("SiteInformationUrlDragFormat", mainWindow);
        Assert.Contains("TryHandleSiteInformationUrlDropAsync", mainWindow);
        Assert.Contains("Background=\"{StaticResource DarkWindowBackgroundBrush}\"", privacyXaml);
        Assert.Contains("Background=\"{StaticResource DarkWindowBackgroundBrush}\"", promptXaml);
        Assert.DoesNotContain("Background=\"White\"", privacyXaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Background=\"White\"", promptXaml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MainWindowKeepsPrivacyUiAndPersistenceOutsideInteractionCoordinator()
    {
        var root = FindRepositoryRoot();
        var mainWindow = File.ReadAllText(Path.Combine(root, "MainWindow.SitePrivacy.cs"));
        var coordinator = File.ReadAllText(
            Path.Combine(root, "Services", "SitePrivacyInteractionCoordinator.cs"));

        Assert.Contains(
            "private readonly SitePrivacyInteractionCoordinator _sitePrivacyInteractionCoordinator = new();",
            mainWindow);
        Assert.Contains("_sitePrivacyInteractionCoordinator.GetPrivatePopupPolicy(sourceUrl)", mainWindow);
        Assert.Contains("_sitePrivacyInteractionCoordinator.SetPrivatePopupPolicy(sourceUrl, policy)", mainWindow);
        Assert.Contains("_sitePrivacyInteractionCoordinator.SetPendingBlockedPopup(", mainWindow);
        Assert.Contains("_sitePrivacyInteractionCoordinator.TakePendingBlockedPopup()", mainWindow);
        Assert.Contains("_sitePrivacyInteractionCoordinator.BeginSiteInformation(", mainWindow);
        Assert.Contains("_sitePrivacyInteractionCoordinator.CloseSiteInformation();", mainWindow);
        Assert.Contains("SitePrivacySettingsProfile.RecordPopupPolicy(", mainWindow);
        Assert.Contains("SettingsService.Save(_settings);", mainWindow);
        Assert.Contains("Dictionary<Guid, List<SitePermissionPromptWindow>> _sitePermissionPrompts", mainWindow);
        Assert.Contains("SiteInformationPopup.IsOpen", mainWindow);
        Assert.Contains("SitePrivacyService.GetPermissionEntriesAsync(webViewProfile)", mainWindow);
        Assert.Contains("deferral.Complete();", mainWindow);
        Assert.DoesNotContain("System.Windows", coordinator);
        Assert.DoesNotContain("Microsoft.Web.WebView2", coordinator);
        Assert.DoesNotContain("SettingsService", coordinator);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TabBrowser.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("TabBrowser.csproj が見つかりません。");
    }

    private sealed class TempSettingsDirectory : IDisposable
    {
        public TempSettingsDirectory()
        {
            Directory.CreateDirectory(DirectoryPath);
        }

        public string DirectoryPath { get; } = Path.Combine(
            Path.GetTempPath(),
            "TabBrowserSitePrivacyTests",
            Guid.NewGuid().ToString("N"));

        public string SettingsFile => Path.Combine(DirectoryPath, "settings.json");

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }
}
