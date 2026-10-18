using System.Text.Json;

namespace TabBrowser.Services;

internal enum YouTubeMiniPlayerWebMessageKind
{
    Unknown,
    Drag,
    Hover,
    HideToolbar,
    ToggleFullScreen,
    Gesture,
    HoldSeek,
    RightWheel,
    SideButton,
    ContentTransform,
    CloseGesture,
    LiveContinuation
}

internal enum YouTubeMiniPlayerDragPhase
{
    Unknown,
    Start,
    Move,
    End
}

internal enum YouTubeMiniPlayerRightInputPhase
{
    Unknown,
    Start,
    Wheel,
    End
}

internal enum YouTubeMiniPlayerGesturePhase
{
    Unknown,
    Start,
    Complete,
    Cancel
}

internal enum YouTubeMiniPlayerSideButton
{
    Unknown,
    XButton1,
    XButton2
}

internal readonly record struct YouTubeMiniPlayerWebMessage(
    YouTubeMiniPlayerWebMessageKind Kind,
    YouTubeMiniPlayerDragPhase DragPhase,
    YouTubeMiniPlayerRightInputPhase RightInputPhase,
    double WheelDelta,
    bool Hovered,
    bool TitleHovered,
    string? Token,
    YouTubeMiniPlayerGesturePhase GesturePhase = YouTubeMiniPlayerGesturePhase.Unknown,
    string? Route = null,
    double ContentZoomFactor = 1,
    double ContentPanX = 0,
    double ContentPanY = 0,
    double ContentAnchorX = 0,
    double ContentAnchorY = 0,
    YouTubeMiniPlayerSideButton SideButton = YouTubeMiniPlayerSideButton.Unknown,
    bool IsResize = false,
    string? SourceVideoId = null,
    string? NextVideoId = null);

internal static class YouTubeMiniPlayerWebMessageService
{
    public static bool TryParse(string json, out YouTubeMiniPlayerWebMessage message)
    {
        message = default;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var type = GetJsonString(root, "type");
            if (string.Equals(type, "youtubeMiniPlayerLiveContinuation", StringComparison.Ordinal))
            {
                message = new YouTubeMiniPlayerWebMessage(
                    YouTubeMiniPlayerWebMessageKind.LiveContinuation,
                    YouTubeMiniPlayerDragPhase.Unknown,
                    YouTubeMiniPlayerRightInputPhase.Unknown,
                    WheelDelta: 0,
                    Hovered: false,
                    TitleHovered: false,
                    Token: GetJsonString(root, "token"),
                    SourceVideoId: GetJsonString(root, "sourceVideoId"),
                    NextVideoId: GetJsonString(root, "nextVideoId"));
                return true;
            }

            if (string.Equals(type, "youtubeMiniPlayerDrag", StringComparison.Ordinal))
            {
                message = new YouTubeMiniPlayerWebMessage(
                    YouTubeMiniPlayerWebMessageKind.Drag,
                    ParseDragPhase(GetJsonString(root, "phase")),
                    YouTubeMiniPlayerRightInputPhase.Unknown,
                    WheelDelta: 0,
                    Hovered: false,
                    TitleHovered: false,
                    Token: null,
                    IsResize: GetJsonBool(root, "resize"));
                return true;
            }

            if (string.Equals(type, "youtubeMiniPlayerHover", StringComparison.Ordinal))
            {
                message = new YouTubeMiniPlayerWebMessage(
                    YouTubeMiniPlayerWebMessageKind.Hover,
                    YouTubeMiniPlayerDragPhase.Unknown,
                    YouTubeMiniPlayerRightInputPhase.Unknown,
                    WheelDelta: 0,
                    GetJsonBool(root, "hovered"),
                    GetJsonBool(root, "titleHovered"),
                    Token: null);
                return true;
            }

            if (string.Equals(type, "youtubeMiniPlayerHideToolbar", StringComparison.Ordinal))
            {
                message = new YouTubeMiniPlayerWebMessage(
                    YouTubeMiniPlayerWebMessageKind.HideToolbar,
                    YouTubeMiniPlayerDragPhase.Unknown,
                    YouTubeMiniPlayerRightInputPhase.Unknown,
                    WheelDelta: 0,
                    Hovered: false,
                    TitleHovered: false,
                    Token: GetJsonString(root, "token"));
                return true;
            }

            if (string.Equals(type, "youtubeMiniPlayerToggleFullScreen", StringComparison.Ordinal))
            {
                message = new YouTubeMiniPlayerWebMessage(
                    YouTubeMiniPlayerWebMessageKind.ToggleFullScreen,
                    YouTubeMiniPlayerDragPhase.Unknown,
                    YouTubeMiniPlayerRightInputPhase.Unknown,
                    WheelDelta: 0,
                    Hovered: false,
                    TitleHovered: false,
                    Token: GetJsonString(root, "token"));
                return true;
            }

            if (string.Equals(type, "youtubeMiniPlayerHoldSeek", StringComparison.Ordinal))
            {
                message = new YouTubeMiniPlayerWebMessage(
                    YouTubeMiniPlayerWebMessageKind.HoldSeek,
                    YouTubeMiniPlayerDragPhase.Unknown,
                    ParseRightInputPhase(GetJsonString(root, "phase")),
                    WheelDelta: 0,
                    Hovered: false,
                    TitleHovered: false,
                    Token: GetJsonString(root, "token"));
                return true;
            }

            if (string.Equals(type, "youtubeMiniPlayerGesture", StringComparison.Ordinal))
            {
                message = new YouTubeMiniPlayerWebMessage(
                    YouTubeMiniPlayerWebMessageKind.Gesture,
                    YouTubeMiniPlayerDragPhase.Unknown,
                    YouTubeMiniPlayerRightInputPhase.Unknown,
                    WheelDelta: 0,
                    Hovered: false,
                    TitleHovered: false,
                    Token: GetJsonString(root, "token"),
                    GesturePhase: ParseGesturePhase(GetJsonString(root, "phase")),
                    Route: GetJsonString(root, "route"));
                return true;
            }

            if (string.Equals(type, "youtubeMiniPlayerRightWheel", StringComparison.Ordinal))
            {
                message = new YouTubeMiniPlayerWebMessage(
                    YouTubeMiniPlayerWebMessageKind.RightWheel,
                    YouTubeMiniPlayerDragPhase.Unknown,
                    ParseRightInputPhase(GetJsonString(root, "phase")),
                    GetJsonDouble(root, "delta"),
                    Hovered: false,
                    TitleHovered: false,
                    Token: GetJsonString(root, "token"),
                    ContentAnchorX: GetJsonDouble(root, "anchorX"),
                    ContentAnchorY: GetJsonDouble(root, "anchorY"));
                return true;
            }

            if (string.Equals(type, "youtubeMiniPlayerSideButton", StringComparison.Ordinal))
            {
                message = new YouTubeMiniPlayerWebMessage(
                    YouTubeMiniPlayerWebMessageKind.SideButton,
                    YouTubeMiniPlayerDragPhase.Unknown,
                    YouTubeMiniPlayerRightInputPhase.Unknown,
                    WheelDelta: 0,
                    Hovered: false,
                    TitleHovered: false,
                    Token: GetJsonString(root, "token"),
                    SideButton: ParseSideButton(GetJsonString(root, "button")));
                return true;
            }

            if (string.Equals(type, "youtubeMiniPlayerContentTransform", StringComparison.Ordinal))
            {
                message = new YouTubeMiniPlayerWebMessage(
                    YouTubeMiniPlayerWebMessageKind.ContentTransform,
                    YouTubeMiniPlayerDragPhase.Unknown,
                    YouTubeMiniPlayerRightInputPhase.Unknown,
                    WheelDelta: 0,
                    Hovered: false,
                    TitleHovered: false,
                    Token: GetJsonString(root, "token"),
                    ContentZoomFactor: GetJsonDouble(root, "zoomFactor"),
                    ContentPanX: GetJsonDouble(root, "panX"),
                    ContentPanY: GetJsonDouble(root, "panY"));
                return true;
            }

            if (string.Equals(type, "youtubeMiniPlayerCloseGesture", StringComparison.Ordinal))
            {
                message = new YouTubeMiniPlayerWebMessage(
                    YouTubeMiniPlayerWebMessageKind.CloseGesture,
                    YouTubeMiniPlayerDragPhase.Unknown,
                    YouTubeMiniPlayerRightInputPhase.Unknown,
                    WheelDelta: 0,
                    Hovered: false,
                    TitleHovered: false,
                    Token: GetJsonString(root, "token"));
                return true;
            }
        }
        catch
        {
            // ミニプレイヤーの表示補助通知なので、失敗しても再生は継続します。
        }

        return false;
    }

    private static YouTubeMiniPlayerDragPhase ParseDragPhase(string? value)
    {
        return value switch
        {
            "start" => YouTubeMiniPlayerDragPhase.Start,
            "move" => YouTubeMiniPlayerDragPhase.Move,
            "end" => YouTubeMiniPlayerDragPhase.End,
            _ => YouTubeMiniPlayerDragPhase.Unknown
        };
    }

    private static YouTubeMiniPlayerRightInputPhase ParseRightInputPhase(string? value)
    {
        return value switch
        {
            "start" => YouTubeMiniPlayerRightInputPhase.Start,
            "wheel" => YouTubeMiniPlayerRightInputPhase.Wheel,
            "end" => YouTubeMiniPlayerRightInputPhase.End,
            _ => YouTubeMiniPlayerRightInputPhase.Unknown
        };
    }

    private static YouTubeMiniPlayerGesturePhase ParseGesturePhase(string? value)
    {
        return value switch
        {
            "start" => YouTubeMiniPlayerGesturePhase.Start,
            "complete" => YouTubeMiniPlayerGesturePhase.Complete,
            "cancel" => YouTubeMiniPlayerGesturePhase.Cancel,
            _ => YouTubeMiniPlayerGesturePhase.Unknown
        };
    }

    private static YouTubeMiniPlayerSideButton ParseSideButton(string? value)
    {
        return value switch
        {
            "xButton1" => YouTubeMiniPlayerSideButton.XButton1,
            "xButton2" => YouTubeMiniPlayerSideButton.XButton2,
            _ => YouTubeMiniPlayerSideButton.Unknown
        };
    }

    private static string? GetJsonString(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static bool GetJsonBool(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;
    }

    private static double GetJsonDouble(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetDouble(out var value)
            && double.IsFinite(value)
                ? value
                : 0;
    }
}
