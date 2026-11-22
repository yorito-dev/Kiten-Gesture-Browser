using System.Windows.Threading;

namespace TabBrowser.Services;

internal sealed class YouTubeMiniPlayerOverlayState
{
    private int _placementGeneration;
    private int _toolbarPlacementRequestGeneration;

    public DispatcherTimer? TitleBandHideTimer { get; set; }
    public DispatcherTimer? ToolbarHideTimer { get; set; }
    public bool IsToolbarMenuOpen { get; set; }
    public bool IsNativePositionRefreshQueued { get; set; }

    public void InvalidatePlacement() => _placementGeneration++;

    public (int PlacementGeneration, int RequestGeneration) BeginToolbarPlacementRequest()
        => (_placementGeneration, ++_toolbarPlacementRequestGeneration);

    public bool IsCurrentToolbarPlacementRequest(int placementGeneration, int requestGeneration)
        => _placementGeneration == placementGeneration
            && _toolbarPlacementRequestGeneration == requestGeneration;
}
