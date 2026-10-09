using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace TabBrowser.Views;

// 通常／分割パネルとミニプレイヤーで共有する表示値・サイズ・境界内配置の規則。
internal static class YouTubeControlPanelPresentation
{
    internal const double YouTubeControlPopupFallbackWidth = 176;
    internal const double YouTubeControlPopupFallbackHeight = 36;
    internal const double YouTubeControlPopupEdgeMargin = 12;
    internal const double YouTubeControlPopupFullscreenEdgeMargin = 4;

    internal static void SetYouTubeSpeedResetContent(Button button, double? speed)
    {
        button.Content = speed.HasValue && double.IsFinite(speed.Value)
            ? $"{speed.Value:0.##}x"
            : "1x";
    }

    internal static void SetYouTubeLoopContent(Button button, bool? isLooping)
    {
        button.Content = isLooping == true
            ? "リピON"
            : "リピOFF";
    }

    internal static (double Width, double Height) GetYouTubeControlPanelSize(FrameworkElement panel)
    {
        var panelWidth = panel.ActualWidth;
        var panelHeight = panel.ActualHeight;

        if (!double.IsFinite(panelWidth) || panelWidth < 1)
        {
            panelWidth = YouTubeControlPopupFallbackWidth;
        }

        if (!double.IsFinite(panelHeight) || panelHeight < 1)
        {
            panelHeight = YouTubeControlPopupFallbackHeight;
        }

        return (panelWidth, panelHeight);
    }

    internal static void SetSplitYouTubeControlPopupPlacement(
        Popup popup,
        FrameworkElement panel,
        FrameworkElement host,
        double horizontalOffset,
        double verticalOffset,
        FrameworkElement webHost,
        Point hostOrigin)
    {
        var hostWidth = Math.Max(1, host.ActualWidth);
        var hostHeight = Math.Max(1, host.ActualHeight);
        var panelSize = GetYouTubeControlPanelSize(panel);
        var minHorizontalOffset = Math.Max(0, hostOrigin.X + YouTubeControlPopupEdgeMargin);
        var maxHorizontalOffset = Math.Max(
            minHorizontalOffset,
            hostOrigin.X + hostWidth - panelSize.Width - YouTubeControlPopupEdgeMargin);
        var minVerticalOffset = Math.Max(0, hostOrigin.Y + YouTubeControlPopupEdgeMargin);
        var maxVerticalOffset = Math.Max(
            minVerticalOffset,
            hostOrigin.Y + hostHeight - panelSize.Height - YouTubeControlPopupEdgeMargin);

        popup.PlacementTarget = webHost;
        popup.HorizontalOffset = Math.Max(minHorizontalOffset, Math.Min(horizontalOffset, maxHorizontalOffset));
        popup.VerticalOffset = Math.Max(minVerticalOffset, Math.Min(verticalOffset, maxVerticalOffset));
    }

    internal static void SetYouTubeControlPopupPlacement(
        Popup popup, FrameworkElement panel, FrameworkElement webHost,
        double horizontalOffset, double verticalOffset, bool isApplicationFullscreenActive)
    {
        var hostWidth = Math.Max(1, webHost.ActualWidth);
        var hostHeight = Math.Max(1, webHost.ActualHeight);
        var panelSize = GetYouTubeControlPanelSize(panel);
        var verticalEdgeMargin = isApplicationFullscreenActive
            ? YouTubeControlPopupFullscreenEdgeMargin
            : YouTubeControlPopupEdgeMargin;
        var maxHorizontalOffset = Math.Max(
            YouTubeControlPopupEdgeMargin,
            hostWidth - panelSize.Width - YouTubeControlPopupEdgeMargin);
        var maxVerticalOffset = Math.Max(
            verticalEdgeMargin,
            hostHeight - panelSize.Height - verticalEdgeMargin);

        popup.HorizontalOffset = Math.Max(
            YouTubeControlPopupEdgeMargin,
            Math.Min(horizontalOffset, maxHorizontalOffset));
        popup.VerticalOffset = Math.Max(
            verticalEdgeMargin,
            Math.Min(verticalOffset, maxVerticalOffset));
    }
}
