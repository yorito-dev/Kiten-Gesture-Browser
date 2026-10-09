using TabBrowser.Services;
using Xunit;

namespace TabBrowser.Tests;

public sealed class BrowserUrlRulesServiceTests
{
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
