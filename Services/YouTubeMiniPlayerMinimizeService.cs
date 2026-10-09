using System.Windows;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal static class YouTubeMiniPlayerMinimizeService
{
    public static void SetMinimized(
        YouTubeMiniPlayerRuntime runtime,
        bool isMinimized,
        Action<YouTubeMiniPlayerRuntime> updateTitleBandPlacement,
        Action<YouTubeMiniPlayerState, UIElement> setPosition)
    {
        runtime.State.IsMinimized = isMinimized;
        runtime.State.UpdatedAt = DateTimeOffset.UtcNow;
        ApplyState(runtime, updateTitleBandPlacement, setPosition);
    }

    public static void ApplyState(
        YouTubeMiniPlayerRuntime runtime,
        Action<YouTubeMiniPlayerRuntime> updateTitleBandPlacement,
        Action<YouTubeMiniPlayerState, UIElement> setPosition,
        bool activate = true)
    {
        _ = updateTitleBandPlacement;
        UpdateMinimizeButton(runtime);
        runtime.ToolbarPopup.IsOpen = false;
        runtime.TitleBandPopup.IsOpen = false;
        runtime.Toolbar.Visibility = Visibility.Collapsed;
        runtime.TitleBand.Visibility = Visibility.Collapsed;
        runtime.ToolbarHideTimer?.Stop();
        runtime.TitleBandHideTimer?.Stop();
        if (runtime.State.IsMinimized)
        {
            if (runtime.TaskbarWindow.WindowState != WindowState.Minimized)
            {
                runtime.TaskbarWindow.WindowState = WindowState.Minimized;
            }

            UpdateTaskbarWindow(runtime, updateSurface: !runtime.IsFullScreen);
            return;
        }

        if (!runtime.IsFullScreen)
        {
            runtime.Root.Width = runtime.State.Width;
            runtime.Root.Height = runtime.State.Height;
            setPosition(runtime.State, runtime.Root);
        }

        UpdateTaskbarWindow(runtime, updateSurface: !runtime.IsFullScreen);
        if (!runtime.TaskbarWindow.IsVisible)
        {
            runtime.TaskbarWindow.Show();
        }

        if (runtime.IsFullScreen)
        {
            runtime.Root.Width = double.NaN;
            runtime.Root.Height = double.NaN;
            runtime.TaskbarWindow.WindowState = WindowState.Maximized;
        }
        else if (runtime.TaskbarWindow.WindowState == WindowState.Minimized)
        {
            runtime.TaskbarWindow.WindowState = WindowState.Normal;
        }

        if (activate)
        {
            YouTubeMiniPlayerPopupService.ActivateRuntimeWindow(runtime);
        }
    }

    public static string CreateStatus(bool isMinimized)
    {
        return isMinimized
            ? "YouTubeミニプレイヤーを最小化しました"
            : "YouTubeミニプレイヤーを復帰しました";
    }

    private static void UpdateMinimizeButton(YouTubeMiniPlayerRuntime runtime)
    {
        runtime.TitleBandMinimizeButton.Content = "最小化";
        runtime.TitleBandMinimizeButton.ToolTip = runtime.State.IsMinimized
            ? "タスクバーからミニプレイヤーを復帰できます"
            : "ミニプレイヤーをタスクバーへ最小化";
    }

    private static void UpdateTaskbarWindow(
        YouTubeMiniPlayerRuntime runtime,
        bool updateSurface)
    {
        var title = YouTubeMiniPlayerNavigationService.NormalizeTitle(
            runtime.State.Title,
            runtime.State.Url);
        runtime.TaskbarWindow.Title = title;
        runtime.TaskbarTitleText.Text = title;
        if (!runtime.TaskbarWindow.IsVisible)
        {
            runtime.TaskbarWindow.Show();
        }

        if (updateSurface)
        {
            SetTaskbarWindowSurface(runtime);
        }
    }

    private static void SetTaskbarWindowSurface(YouTubeMiniPlayerRuntime runtime)
    {
        var width = Math.Max(YouTubeMiniPlayerLayoutService.MinimumWidth, runtime.State.Width);
        var height = Math.Max(YouTubeMiniPlayerLayoutService.MinimumHeight, runtime.State.Height);
        if (runtime.TaskbarWindow.Content is FrameworkElement root)
        {
            root.Width = width;
            root.Height = height;
        }

        runtime.TaskbarWindow.Width = width;
        runtime.TaskbarWindow.Height = height;
        runtime.TaskbarWindow.Opacity = 1;
        runtime.TaskbarWindow.UpdateLayout();
    }
}
