using TabBrowser.Models;
using Xunit;

namespace TabBrowser.Tests;

public sealed class YouTubeMiniPlayerContentTransformStateTests
{
    [Fact]
    public void NormalizeUsesBackwardCompatibleDefaultsAndClearsPanAtOriginalSize()
    {
        var state = new YouTubeMiniPlayerState
        {
            ContentZoomFactor = double.NaN,
            ContentPanX = 0.8,
            ContentPanY = -0.6
        };

        YouTubeMiniPlayerContentTransformState.Normalize(state);

        Assert.Equal(1, state.ContentZoomFactor);
        Assert.Equal(0, state.ContentPanX);
        Assert.Equal(0, state.ContentPanY);
    }

    [Fact]
    public void ChangeZoomUsesTenPercentStepsAndClampsRange()
    {
        var state = new YouTubeMiniPlayerState();

        YouTubeMiniPlayerContentTransformState.ChangeZoom(state, 1);
        Assert.Equal(1.1, state.ContentZoomFactor);

        YouTubeMiniPlayerContentTransformState.ChangeZoom(state, 100);
        Assert.Equal(4, state.ContentZoomFactor);

        YouTubeMiniPlayerContentTransformState.ChangeZoom(state, -100);
        Assert.Equal(1, state.ContentZoomFactor);
    }

    [Fact]
    public void ChangeZoomAtAnchorKeepsPointedContentPosition()
    {
        var state = new YouTubeMiniPlayerState();

        Assert.True(YouTubeMiniPlayerContentTransformState.ChangeZoomAtAnchor(
            state,
            stepCount: 10,
            anchorX: 0.5,
            anchorY: -0.5));
        Assert.Equal(2, state.ContentZoomFactor);
        Assert.Equal(-0.5, state.ContentPanX, 10);
        Assert.Equal(0.5, state.ContentPanY, 10);

        Assert.True(YouTubeMiniPlayerContentTransformState.ChangeZoomAtAnchor(
            state,
            stepCount: 10,
            anchorX: 0.5,
            anchorY: -0.5));
        Assert.Equal(3, state.ContentZoomFactor);
        Assert.Equal(-0.5, state.ContentPanX, 10);
        Assert.Equal(0.5, state.ContentPanY, 10);
    }

    [Fact]
    public void MovePanUsesNaturalDragDirectionAndClampsToVisibleCrop()
    {
        var state = new YouTubeMiniPlayerState { ContentZoomFactor = 2 };

        YouTubeMiniPlayerContentTransformState.MovePan(
            state,
            startPanX: 0,
            startPanY: 0,
            deltaX: 500,
            deltaY: -500,
            viewportWidth: 400,
            viewportHeight: 200);

        Assert.Equal(1, state.ContentPanX);
        Assert.Equal(-1, state.ContentPanY);
    }

    [Fact]
    public void MovePanReportsAttemptBeyondCropBoundary()
    {
        var state = new YouTubeMiniPlayerState { ContentZoomFactor = 2 };

        YouTubeMiniPlayerContentTransformState.MovePan(
            state,
            startPanX: 0,
            startPanY: 0,
            deltaX: 500,
            deltaY: 0,
            viewportWidth: 400,
            viewportHeight: 200,
            out var reachedBoundary);

        Assert.True(reachedBoundary);
        Assert.Equal(1, state.ContentPanX);
    }

    [Fact]
    public void CropGeometryKeepsCenterAndMapsDraggedContentPosition()
    {
        Assert.Equal(
            "480+270+480+270",
            YouTubeMiniPlayerContentTransformState.CreateCropGeometry(1920, 1080, 2, 0, 0));
        Assert.Equal(
            "0+540+960+0",
            YouTubeMiniPlayerContentTransformState.CreateCropGeometry(1920, 1080, 2, 1, -1));
        Assert.Equal(
            string.Empty,
            YouTubeMiniPlayerContentTransformState.CreateCropGeometry(1920, 1080, 1, 1, -1));
    }

    [Fact]
    public void ResetRestoresOriginalSizeAndCenteredPosition()
    {
        var state = new YouTubeMiniPlayerState
        {
            ContentZoomFactor = 3.2,
            ContentPanX = 0.5,
            ContentPanY = -0.25
        };

        Assert.True(YouTubeMiniPlayerContentTransformState.Reset(state));
        Assert.Equal("100%", YouTubeMiniPlayerContentTransformState.FormatPercentage(state.ContentZoomFactor));
        Assert.Equal(0, state.ContentPanX);
        Assert.Equal(0, state.ContentPanY);
    }

    [Fact]
    public void ToggleOriginalSizeSwitchesBetweenOriginalAndPreviousZoomAtCenter()
    {
        var state = new YouTubeMiniPlayerState
        {
            ContentZoomFactor = 2.3,
            ContentPanX = 0.5,
            ContentPanY = -0.25
        };

        Assert.True(YouTubeMiniPlayerContentTransformState.ToggleOriginalSize(state));
        Assert.Equal(1, state.ContentZoomFactor);
        Assert.Equal(2.3, state.PreviousContentZoomFactor);
        Assert.Equal(0, state.ContentPanX);
        Assert.Equal(0, state.ContentPanY);

        Assert.True(YouTubeMiniPlayerContentTransformState.ToggleOriginalSize(state));
        Assert.Equal(2.3, state.ContentZoomFactor);
        Assert.Equal(2.3, state.PreviousContentZoomFactor);
        Assert.Equal(0, state.ContentPanX);
        Assert.Equal(0, state.ContentPanY);
    }

    [Fact]
    public void ToggleOriginalSizeKeepsOriginalSizeWithoutPreviousZoom()
    {
        var state = new YouTubeMiniPlayerState();

        Assert.False(YouTubeMiniPlayerContentTransformState.ToggleOriginalSize(state));
        Assert.Equal(1, state.ContentZoomFactor);
        Assert.Null(state.PreviousContentZoomFactor);
    }
}
