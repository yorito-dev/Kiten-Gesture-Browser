using System.Reflection;
using TabBrowser.Services;
using Xunit;

namespace TabBrowser.Tests;

public sealed class YouTubeMiniPlayerWebMessageServiceTests
{
    [Fact]
    public void TryParseReadsAuthenticatedLiveContinuationWithoutAcceptingArbitraryUrl()
    {
        const string json = """{"type":"youtubeMiniPlayerLiveContinuation","token":"test-token","sourceVideoId":"oldLive1234","channelId":"UCaaaaaaaaaaaaaaaaaaaaaa","url":"https://example.com/"}""";
        var (parsed, message) = Parse(json);

        Assert.True(parsed);
        Assert.Equal("LiveContinuation", ReadProperty(message, "Kind")?.ToString());
        Assert.Equal("test-token", ReadProperty(message, "Token"));
        Assert.Equal("oldLive1234", ReadProperty(message, "SourceVideoId"));
        Assert.Equal("UCaaaaaaaaaaaaaaaaaaaaaa", ReadProperty(message, "ChannelId"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TryParseReadsMiniPlayerDragResizeMode(bool resize)
    {
        var json = $$"""{"type":"youtubeMiniPlayerDrag","phase":"start","resize":{{resize.ToString().ToLowerInvariant()}}}""";

        var (parsed, message) = Parse(json);

        Assert.True(parsed);
        Assert.Equal("Drag", ReadProperty(message, "Kind")?.ToString());
        Assert.Equal("Start", ReadProperty(message, "DragPhase")?.ToString());
        Assert.Equal(resize, ReadProperty(message, "IsResize"));
    }

    [Fact]
    public void TryParseReadsAuthenticatedHoldSeekStart()
    {
        const string json = """{"type":"youtubeMiniPlayerHoldSeek","phase":"start","token":"test-token"}""";

        var (parsed, message) = Parse(json);

        Assert.True(parsed);
        Assert.Equal("HoldSeek", ReadProperty(message, "Kind")?.ToString());
        Assert.Equal("Start", ReadProperty(message, "RightInputPhase")?.ToString());
        Assert.Equal("test-token", ReadProperty(message, "Token"));
    }

    [Fact]
    public void TryParseReadsRightWheelDeltaAndEnd()
    {
        const string wheelJson = """{"type":"youtubeMiniPlayerRightWheel","phase":"wheel","delta":120.5,"anchorX":0.75,"anchorY":-0.4,"token":"test-token"}""";
        const string endJson = """{"type":"youtubeMiniPlayerRightWheel","phase":"end","token":"test-token"}""";

        var (wheelParsed, wheelMessage) = Parse(wheelJson);
        var (endParsed, endMessage) = Parse(endJson);

        Assert.True(wheelParsed);
        Assert.Equal("RightWheel", ReadProperty(wheelMessage, "Kind")?.ToString());
        Assert.Equal("Wheel", ReadProperty(wheelMessage, "RightInputPhase")?.ToString());
        Assert.Equal(120.5, ReadProperty(wheelMessage, "WheelDelta"));
        Assert.Equal(0.75, ReadProperty(wheelMessage, "ContentAnchorX"));
        Assert.Equal(-0.4, ReadProperty(wheelMessage, "ContentAnchorY"));
        Assert.True(endParsed);
        Assert.Equal("End", ReadProperty(endMessage, "RightInputPhase")?.ToString());
    }

    [Fact]
    public void TryParseReadsAuthenticatedCloseGesture()
    {
        const string json = """{"type":"youtubeMiniPlayerCloseGesture","token":"test-token"}""";

        var (parsed, message) = Parse(json);

        Assert.True(parsed);
        Assert.Equal("CloseGesture", ReadProperty(message, "Kind")?.ToString());
        Assert.Equal("test-token", ReadProperty(message, "Token"));
    }

    [Fact]
    public void TryParseReadsAuthenticatedHideToolbarRequest()
    {
        const string json = """{"type":"youtubeMiniPlayerHideToolbar","token":"test-token"}""";

        var (parsed, message) = Parse(json);

        Assert.True(parsed);
        Assert.Equal("HideToolbar", ReadProperty(message, "Kind")?.ToString());
        Assert.Equal("test-token", ReadProperty(message, "Token"));
    }

    [Fact]
    public void TryParseReadsAuthenticatedContentTransform()
    {
        const string json = """{"type":"youtubeMiniPlayerContentTransform","zoomFactor":2.5,"panX":0.4,"panY":-0.3,"token":"test-token"}""";

        var (parsed, message) = Parse(json);

        Assert.True(parsed);
        Assert.Equal("ContentTransform", ReadProperty(message, "Kind")?.ToString());
        Assert.Equal(2.5, ReadProperty(message, "ContentZoomFactor"));
        Assert.Equal(0.4, ReadProperty(message, "ContentPanX"));
        Assert.Equal(-0.3, ReadProperty(message, "ContentPanY"));
        Assert.Equal("test-token", ReadProperty(message, "Token"));
    }

    [Fact]
    public void TryParseReadsAuthenticatedMiniPlayerGesture()
    {
        const string json = """{"type":"youtubeMiniPlayerGesture","phase":"complete","route":"←↑","token":"test-token"}""";

        var (parsed, message) = Parse(json);

        Assert.True(parsed);
        Assert.Equal("Gesture", ReadProperty(message, "Kind")?.ToString());
        Assert.Equal("Complete", ReadProperty(message, "GesturePhase")?.ToString());
        Assert.Equal("←↑", ReadProperty(message, "Route"));
        Assert.Equal("test-token", ReadProperty(message, "Token"));
    }

    [Theory]
    [InlineData("xButton1", "XButton1")]
    [InlineData("xButton2", "XButton2")]
    public void TryParseReadsAuthenticatedSideButton(string button, string expected)
    {
        var json = $$"""{"type":"youtubeMiniPlayerSideButton","button":"{{button}}","token":"test-token"}""";

        var (parsed, message) = Parse(json);

        Assert.True(parsed);
        Assert.Equal("SideButton", ReadProperty(message, "Kind")?.ToString());
        Assert.Equal(expected, ReadProperty(message, "SideButton")?.ToString());
        Assert.Equal("test-token", ReadProperty(message, "Token"));
    }

    private static (bool Parsed, object Message) Parse(string json)
    {
        var serviceType = typeof(YouTubeControlScriptService).Assembly.GetType(
            "TabBrowser.Services.YouTubeMiniPlayerWebMessageService",
            throwOnError: true)!;
        var tryParse = serviceType.GetMethod(
            "TryParse",
            BindingFlags.Public | BindingFlags.Static)!;
        var arguments = new object?[] { json, null };
        var parsed = (bool)tryParse.Invoke(null, arguments)!;
        return (parsed, arguments[1]!);
    }

    private static object? ReadProperty(object target, string propertyName)
    {
        return target.GetType().GetProperty(propertyName)?.GetValue(target);
    }
}
