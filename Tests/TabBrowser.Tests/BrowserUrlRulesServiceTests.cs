using TabBrowser.Services;
using Xunit;

namespace TabBrowser.Tests;

public sealed class BrowserUrlRulesServiceTests
{
    [Theory]
    [InlineData("http://fixture.test/page", "http://fixture.test/page", true)]
    [InlineData("https://fixture.test/page", "https://FIXTURE.test/page", true)]
    [InlineData("https://fixture.test/page", "https://elsewhere.test/page", false)]
    [InlineData("https://fixture.test/page", "https://fixture.test/frame", false)]
    [InlineData("https://fixture.test/page", null, false)]
    [InlineData("about:blank", "about:blank", false)]
    [InlineData("file:///C:/fixture.html", "file:///C:/fixture.html", false)]
    public void BlankPopupOpenerIsPreservedOnlyForTheCurrentWebPage(string currentUrl, string? requestFrame, bool expected)
        => Assert.Equal(expected, BrowserUrlRulesService.IsBlankPopupFromCurrentWebPage(currentUrl, requestFrame));

    [Theory]
    [InlineData("https://translate.google.com/?sl=auto&text=synthetic&op=translate", true)]
    [InlineData("https://translate.google.com/#sl=auto&%74ext=synthetic", true)]
    [InlineData("https://translate.google.com/?text=", false)]
    [InlineData("https://translate.google.com/translate?u=https%3A%2F%2Fexample.test%2F", false)]
    [InlineData("https://translate.google.com.example.test/?text=synthetic", false)]
    [InlineData("https://translate.google.com/?context=synthetic", false)]
    public void RecognizesOnlyTranslationResultsWithSelectedText(string url, bool expected)
        => Assert.Equal(expected, BrowserUrlRulesService.IsSelectedTextTranslationUrl(url));

    [Theory]
    [InlineData("https://untrusted.invalid/__/auth/handler?authType=signInViaPopup", false, false)]
    [InlineData("https://untrusted.invalid/__/auth/handler?authType=signInViaPopup", true, true)]
    [InlineData("https://untrusted.invalid/__/auth/handler?%61uthType=%73ignInViaPopup", false, false)]
    [InlineData("https://untrusted.invalid/__/auth/handler?%61uthType=%73ignInViaPopup", true, true)]
    [InlineData("https://console.sakana.ai/__/auth/handler?authType=signInViaPopup&providerId=google.com", true, true)]
    [InlineData("https://console.sakana.ai/__/auth/handler?authType=signInViaRedirect", true, false)]
    [InlineData("https://accounts.google.com/o/oauth2/v2/auth", false, true)]
    [InlineData("https://appleid.apple.com/auth/authorize", false, true)]
    [InlineData("https://login.live.com/oauth20_authorize.srf", false, true)]
    [InlineData("https://login.microsoftonline.com/common/oauth2/v2.0/authorize", false, true)]
    [InlineData("https://x.com/i/flow/login", false, true)]
    [InlineData("https://twitter.com/i/oauth2/authorize", false, true)]
    [InlineData("https://accounts.google.com.untrusted.invalid/", true, false)]
    public void AuthenticationPopupRequiresUserInitiationOnlyForHostIndependentFirebase(string url, bool userInitiated, bool expected)
    {
        Assert.Equal(expected, BrowserUrlRulesService.IsAuthenticationPopupUrl(url, userInitiated));
    }

    [Theory]
    [InlineData("https://example.invalid/__/auth/handler?token=private", "example.invalid")]
    [InlineData("https://accounts.google.com/o/oauth2/v2/auth", "accounts.google.com")]
    [InlineData("about:blank", "ログイン")]
    [InlineData(null, "ログイン")]
    [InlineData("invalid", "ログイン")]
    public void AuthenticationPopupInitialTitleShowsOnlyKnownHost(string? url, string expected)
    {
        Assert.Equal(expected, BrowserUrlRulesService.GetAuthenticationPopupInitialTitle(url));
    }

    [Theory]
    [InlineData(
        "https://www.amazon.co.jp/BUFFALO-HD-PCFS2-0U3-BBA/dp/B07D7978NV/ref=sr_1_8?keywords=usb+hdd&sr=1-8",
        "https://www.amazon.co.jp/dp/B07D7978NV")]
    [InlineData(
        "https://www.amazon.co.jp/BUFFALO-HD-PCFS2-0U3-BBA/dp/B07D7978NV?keywords=usb%2Bhdd&th=1",
        "https://www.amazon.co.jp/dp/B07D7978NV")]
    [InlineData(
        "https://www.amazon.co.jp/dp/b07d7978nv/",
        "https://www.amazon.co.jp/dp/B07D7978NV")]
    public void NormalizeNavigationHistoryUrlCanonicalizesAmazonJapanProductUrls(
        string url,
        string expected)
    {
        Assert.Equal(expected, BrowserUrlRulesService.NormalizeNavigationHistoryUrl(url));
    }

    [Theory]
    [InlineData("https://www.amazon.co.jp/s?k=usb+hdd&ref=nb_sb_noss")]
    [InlineData("https://example.com/product/dp/B07D7978NV?ref=tracking")]
    [InlineData("https://www.amazon.co.jp/product/dp/not-an-asin?ref=tracking")]
    public void NormalizeNavigationHistoryUrlLeavesNonProductUrlsUnchanged(string url)
    {
        Assert.Equal(url, BrowserUrlRulesService.NormalizeNavigationHistoryUrl(url));
    }

    [Theory]
    [InlineData("https://x.com/home", true)]
    [InlineData("https://mobile.x.com/home", true)]
    [InlineData("https://twitter.com/home", true)]
    [InlineData("https://mobile.twitter.com/home", true)]
    [InlineData("https://example.com/home", false)]
    public void IsXOrTwitterUrlDetectsSupportedHosts(string url, bool expected)
    {
        Assert.Equal(expected, BrowserUrlRulesService.IsXOrTwitterUrl(url));
    }

    [Theory]
    [InlineData("https://accounts.google.com/o/oauth2/v2/auth", true)]
    [InlineData("https://appleid.apple.com/auth/authorize", true)]
    [InlineData("https://login.live.com/oauth20_authorize.srf", true)]
    [InlineData("https://login.microsoftonline.com/common/oauth2/v2.0/authorize", true)]
    [InlineData("https://console.sakana.ai/__/auth/handler?authType=signInViaPopup&providerId=google.com", true)]
    [InlineData("https://example.com/__/auth/handler?providerId=google.com&authType=signInViaPopup", true)]
    [InlineData("https://console.sakana.ai/__/auth/handler?authType=signInViaRedirect&providerId=google.com", false)]
    [InlineData("https://x.com/i/flow/login", true)]
    [InlineData("https://twitter.com/i/oauth2/authorize", true)]
    [InlineData("https://x.com/home", false)]
    [InlineData("about:blank", false)]
    public void IsAuthenticationPopupUrlDetectsKnownAuthenticationUrls(string url, bool expected)
    {
        Assert.Equal(expected, BrowserUrlRulesService.IsAuthenticationPopupUrl(url));
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abc123", true)]
    [InlineData("https://www.twitch.tv/example", true)]
    [InlineData("https://www.nicovideo.jp/watch/sm9", true)]
    [InlineData("https://www.niconico.com/watch/sm9", true)]
    [InlineData("https://vimeo.com/123456", true)]
    [InlineData("https://www.bilibili.com/video/BV1234567890", true)]
    [InlineData("https://example.com/video", false)]
    public void IsBackgroundMediaPlaybackUrlDetectsKnownMediaHosts(string url, bool expected)
    {
        Assert.Equal(expected, BrowserUrlRulesService.IsBackgroundMediaPlaybackUrl(url));
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abc123", false)]
    [InlineData("https://www.twitch.tv/example", true)]
    [InlineData("https://www.nicovideo.jp/watch/sm9", true)]
    [InlineData("https://vimeo.com/123456", true)]
    [InlineData("https://example.com/video", false)]
    public void ShouldDeferBackgroundMediaLoadKeepsYouTubePreloadEnabled(string url, bool expected)
    {
        Assert.Equal(expected, BrowserUrlRulesService.ShouldDeferBackgroundMediaLoad(url));
    }

    [Theory]
    [InlineData("twitch.tv", "twitch.tv", true)]
    [InlineData("www.twitch.tv", "twitch.tv", true)]
    [InlineData("evil-twitch.tv", "twitch.tv", false)]
    [InlineData("twitch.tv.example.com", "twitch.tv", false)]
    public void HostMatchesOnlyMatchesDomainOrSubdomain(string host, string domain, bool expected)
    {
        Assert.Equal(expected, BrowserUrlRulesService.HostMatches(host, domain));
    }
}
