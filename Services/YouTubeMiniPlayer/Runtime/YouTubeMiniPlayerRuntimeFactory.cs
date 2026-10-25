using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shell;
using Microsoft.Web.WebView2.Wpf;
using TabBrowser.Models;
using TabBrowser.Views;

namespace TabBrowser.Services;

internal sealed record YouTubeMiniPlayerRuntimeParts(
    YouTubeMiniPlayerRuntime Runtime,
    Grid Chrome,
    YouTubeMiniPlayerTitleBandParts TitleBandParts,
    YouTubeMiniPlayerToolbarParts ToolbarParts);

internal static class YouTubeMiniPlayerRuntimeFactory
{
    public static YouTubeMiniPlayerRuntimeParts Create(
        YouTubeMiniPlayerState state,
        Style buttonStyle,
        string title,
        UIElement webContent,
        WebView2? webView,
        LocalVideoPlayerView? localVideoView)
    {
        var popup = new Popup();
        var root = new Border
        {
            Width = state.Width,
            Height = state.Height,
            MinWidth = YouTubeMiniPlayerLayoutService.MinimumWidth,
            MinHeight = YouTubeMiniPlayerLayoutService.MinimumHeight,
            Background = new SolidColorBrush(Color.FromRgb(8, 10, 14)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(138, 180, 248)),
            BorderThickness = new Thickness(YouTubeMiniPlayerLayoutService.FrameBorderThickness),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true
        };

        var chrome = new Grid
        {
            Background = Brushes.Transparent
        };
        var layout = new Grid
        {
            Margin = new Thickness(YouTubeMiniPlayerLayoutService.ResizeBorderThickness)
        };
        var listHighlightBorder = new Border
        {
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        Panel.SetZIndex(listHighlightBorder, int.MaxValue);
        var audioStatusText = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Text = "音声"
        };
        var audioStatusBadge = new Border
        {
            Margin = new Thickness(8),
            Padding = new Thickness(6, 2, 6, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromArgb(224, 22, 101, 52)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(74, 222, 128)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Child = audioStatusText,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        Panel.SetZIndex(audioStatusBadge, int.MaxValue - 1);

        var titleBandParts = YouTubeMiniPlayerChromeFactory.CreateTitleBand(title, buttonStyle);
        if (state.SourceKind == MiniPlayerSourceKind.LocalFile)
        {
            titleBandParts.ChatButton.Visibility = Visibility.Collapsed;
            titleBandParts.SourceButton.Content = "場所";
            titleBandParts.SourceButton.ToolTip = "動画ファイルをExplorerで表示";
        }

        var titleBandPopup = CreateOverlayPopup(titleBandParts.Root);

        var toolbarParts = YouTubeMiniPlayerChromeFactory.CreateToolbar(buttonStyle);
        var toolbarPopup = CreateOverlayPopup(toolbarParts.Root);

        var webHost = new Grid
        {
            Background = Brushes.Black,
            ClipToBounds = true
        };
        webHost.Children.Add(webContent);

        layout.Children.Add(webHost);
        chrome.Children.Add(layout);
        chrome.Children.Add(audioStatusBadge);
        chrome.Children.Add(listHighlightBorder);

        root.Child = chrome;
        var playerWindow = CreatePlayerWindow(title, state, root);

        var runtime = new YouTubeMiniPlayerRuntime(
            state,
            popup,
            root,
            listHighlightBorder,
            audioStatusBadge,
            audioStatusText,
            playerWindow,
            titleBandParts.TitleText,
            titleBandPopup,
            titleBandParts.Root,
            titleBandParts.TitleText,
            titleBandParts.BackButton,
            titleBandParts.ForwardButton,
            titleBandParts.LiveViewerCountButton,
            titleBandParts.LiveViewerCountText,
            titleBandParts.ChatButton,
            titleBandParts.TopmostButton,
            titleBandParts.MinimizeButton,
            titleBandParts.SourceButton,
            titleBandParts.CloseButton,
            toolbarPopup,
            toolbarParts.Root,
            toolbarParts.ZoomResetButton,
            webHost,
            webView,
            localVideoView);
        YouTubeMiniPlayerChromeFactory.UpdateLiveViewerCount(
            runtime,
            state.IsLiveBroadcast,
            state.LiveViewerCountText);
        YouTubeMiniPlayerLayoutService.ApplyMinimumSize(runtime);
        return new YouTubeMiniPlayerRuntimeParts(runtime, chrome, titleBandParts, toolbarParts);
    }

    private static Popup CreateOverlayPopup(UIElement child)
    {
        return new Popup
        {
            AllowsTransparency = true,
            Placement = PlacementMode.AbsolutePoint,
            StaysOpen = true,
            Child = child
        };
    }

    private static Window CreatePlayerWindow(string title, YouTubeMiniPlayerState state, FrameworkElement content)
    {
        var normalizedTitle = YouTubeMiniPlayerNavigationService.NormalizeTitle(title, state.Url);
        var width = GetTaskbarWindowWidth(state);
        var height = GetTaskbarWindowHeight(state);
        var window = new Window
        {
            Title = normalizedTitle,
            Width = width,
            Height = height,
            MinWidth = YouTubeMiniPlayerLayoutService.MinimumWidth,
            MinHeight = YouTubeMiniPlayerLayoutService.MinimumHeight,
            Left = GetTaskbarWindowLeft(state),
            Top = GetTaskbarWindowTop(state),
            WindowStartupLocation = WindowStartupLocation.Manual,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(16, 18, 28)),
            ShowInTaskbar = true,
            ShowActivated = false,
            Topmost = state.IsTopmost,
            Icon = Application.Current?.MainWindow?.Icon,
            Content = content
        };
        WindowChrome.SetWindowChrome(
            window,
            new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(8),
                GlassFrameThickness = new Thickness(0),
                UseAeroCaptionButtons = false
            });
        return window;
    }

    private static double GetTaskbarWindowWidth(YouTubeMiniPlayerState state)
    {
        return Math.Max(240, state.Width);
    }

    private static double GetTaskbarWindowHeight(YouTubeMiniPlayerState state)
    {
        return Math.Max(150, state.Height);
    }

    private static double GetTaskbarWindowLeft(YouTubeMiniPlayerState state)
    {
        return state.Left;
    }

    private static double GetTaskbarWindowTop(YouTubeMiniPlayerState state)
    {
        return state.Top;
    }
}
