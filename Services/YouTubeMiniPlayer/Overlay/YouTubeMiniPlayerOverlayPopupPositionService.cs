using System;
using System.Windows;
using System.Windows.Controls.Primitives;

namespace TabBrowser.Services;

internal static class YouTubeMiniPlayerOverlayPopupPositionService
{
    public static void UpdateTitleBandPlacement(
        YouTubeMiniPlayerRuntime runtime,
        double minimizedWidth,
        double fallbackWidth,
        double edgeMargin)
    {
        if (runtime.State.IsMinimized)
        {
            var titleBandWidth = YouTubeMiniPlayerOverlayPlacementService.GetMinimizedTitleBandWidth(
                runtime.State.Width,
                minimizedWidth);
            YouTubeMiniPlayerChromeFactory.UpdateTitleBandButtonVisibility(runtime, titleBandWidth);
            runtime.TitleBand.MaxWidth = titleBandWidth;
            runtime.TitleBand.Width = titleBandWidth;
            YouTubeMiniPlayerPopupService.SetRelativePosition(
                runtime.TitleBandPopup,
                runtime.Root,
                runtime.TitleBand,
                new Point(0, 0));
            PopupWindowOrderService.ApplyRelativeToOwner(
                runtime.TitleBandPopup,
                runtime.TitleBand,
                runtime.Root);
            return;
        }

        var hostWidth = YouTubeMiniPlayerOverlayPlacementService.GetWebHostWidth(runtime);
        var overlayWidth = YouTubeMiniPlayerOverlayPlacementService.GetOverlayWidth(
            hostWidth,
            fallbackWidth,
            edgeMargin);
        YouTubeMiniPlayerChromeFactory.UpdateTitleBandButtonVisibility(runtime, overlayWidth);
        runtime.TitleBand.MaxWidth = overlayWidth;
        runtime.TitleBand.Width = overlayWidth;
        SetOverlayPopupPosition(
            runtime,
            runtime.TitleBandPopup,
            runtime.TitleBand,
            edgeMargin,
            edgeMargin);
    }

    public static void UpdateToolbarPlacement(
        YouTubeMiniPlayerRuntime runtime,
        double fallbackWidth,
        double edgeMargin,
        double bottomMargin,
        Func<FrameworkElement, (double Width, double Height)> getPanelSize)
    {
        var hostWidth = YouTubeMiniPlayerOverlayPlacementService.GetWebHostWidth(runtime);
        var hostHeight = YouTubeMiniPlayerOverlayPlacementService.GetWebHostHeight(runtime);
        runtime.Toolbar.MaxWidth = YouTubeMiniPlayerOverlayPlacementService.GetOverlayWidth(
            hostWidth,
            fallbackWidth,
            edgeMargin);
        runtime.Toolbar.Width = double.NaN;
        var panelSize = getPanelSize(runtime.Toolbar);
        var offset = YouTubeMiniPlayerOverlayPlacementService.GetDefaultToolbarOffset(
            hostWidth,
            hostHeight,
            panelSize,
            bottomMargin);
        SetToolbarPlacement(
            runtime,
            offset.X,
            offset.Y,
            edgeMargin,
            getPanelSize);
    }

    public static void SetToolbarPlacement(
        YouTubeMiniPlayerRuntime runtime,
        double horizontalOffset,
        double verticalOffset,
        double edgeMargin,
        Func<FrameworkElement, (double Width, double Height)> getPanelSize)
    {
        var hostWidth = YouTubeMiniPlayerOverlayPlacementService.GetWebHostWidth(runtime);
        var hostHeight = YouTubeMiniPlayerOverlayPlacementService.GetWebHostHeight(runtime);
        var panelSize = getPanelSize(runtime.Toolbar);
        var offset = YouTubeMiniPlayerOverlayPlacementService.ClampToolbarOffset(
            horizontalOffset,
            verticalOffset,
            hostWidth,
            hostHeight,
            panelSize,
            edgeMargin);

        SetOverlayPopupPosition(
            runtime,
            runtime.ToolbarPopup,
            runtime.Toolbar,
            offset.X,
            offset.Y);
    }

    public static void SetOverlayPopupPosition(
        YouTubeMiniPlayerRuntime runtime,
        Popup popup,
        FrameworkElement popupRoot,
        double horizontalOffset,
        double verticalOffset)
    {
        YouTubeMiniPlayerPopupService.SetRelativePosition(
            popup,
            runtime.WebHost,
            popupRoot,
            new Point(horizontalOffset, verticalOffset));
        PopupWindowOrderService.ApplyRelativeToOwner(
            popup,
            popupRoot,
            runtime.Root);
    }

    public static void RefreshOverlayPopupPosition(
        YouTubeMiniPlayerRuntime runtime,
        Action<YouTubeMiniPlayerRuntime> updateTitleBandPlacement,
        Action<YouTubeMiniPlayerRuntime> updateToolbarPlacement)
    {
        if (runtime.TitleBandPopup.IsOpen)
        {
            updateTitleBandPlacement(runtime);
        }

        if (runtime.ToolbarPopup.IsOpen)
        {
            updateToolbarPlacement(runtime);
        }
    }
}
