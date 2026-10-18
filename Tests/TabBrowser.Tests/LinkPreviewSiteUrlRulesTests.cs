using TabBrowser.Services;
using Xunit;

namespace TabBrowser.Tests;

public sealed class LinkPreviewSiteUrlRulesTests
{
    [Theory]
    [InlineData("https://chatgpt.com/", true)]
    [InlineData("https://chatgpt.com/c/fictional", true)]
    [InlineData("http://chatgpt.com/", true)]
    [InlineData("https://chatgpt.com.example.invalid/", false)]
    [InlineData("https://help.chatgpt.com/", false)]
    [InlineData("file:///chatgpt.com/", false)]
    [InlineData(null, false)]
    public void ChatGptPreviewExclusionMatchesOnlyTheExactWebHost(string? url, bool expected)
    {
        Assert.Equal(expected, new ReadabilityController().IsChatGptUrl(url));
    }

    [Theory]
    [InlineData("https://example.5ch.io/test/read.cgi/livejupiter/1234567890/", ">>12", true)]
    [InlineData("https://example.5ch.net/test/read.cgi/livejupiter/1234567890/", "＞＞12", true)]
    [InlineData("https://example.5ch.io/test/read.cgi/livejupiter/1234567890/", "外部リンク", false)]
    [InlineData("https://example.5ch.io.example.invalid/test/read.cgi/livejupiter/1234567890/", ">>12", false)]
    [InlineData("https://example.5ch.io/", ">>12", false)]
    public void ReplyAnchorSuppressionIsLimitedToFiveChThreads(string current, string title, bool expected)
    {
        Assert.Equal(expected, new FiveChLiveReadabilityService().IsReplyAnchorLink(current, "https://example.invalid/", title));
    }

    [Theory]
    [InlineData("https://x.com/fixture", true)]
    [InlineData("https://twitter.com/fixture", true)]
    [InlineData("https://mobile.twitter.com/fixture", true)]
    [InlineData("https://www.x.com/fixture", true)]
    [InlineData("https://x.com.example.invalid/", false)]
    [InlineData("https://twitter.com.example.invalid/", false)]
    [InlineData(null, false)]
    public void XPreviewExclusionRetainsExactHostsAndSubdomains(string? url, bool expected)
    {
        Assert.Equal(expected, BrowserUrlRulesService.IsXOrTwitterUrl(url));
    }

    [Theory]
    [InlineData("12")]
    [InlineData("#12")]
    public void ReplyAnchorSuppressionAlsoRecognizesUrlsWithoutReplyLabels(string suffix)
    {
        const string thread = "https://example.5ch.io/test/read.cgi/livejupiter/1234567890/";
        var rules = new FiveChLiveReadabilityService();
        Assert.True(rules.IsReplyAnchorLink(thread, thread + suffix, "返信"));
        Assert.False(rules.IsReplyAnchorLink(thread, "https://example.5ch.io/test/read.cgi/livejupiter/9876543210/12", "返信"));
    }
}
