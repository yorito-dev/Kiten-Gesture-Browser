using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Wpf;
using TabBrowser.Models;
using TabBrowser.Views;

namespace TabBrowser.Services;

internal static class YouTubeMiniPlayerRuntimeCompositionService
{
    public static YouTubeMiniPlayerRuntime Create(
        YouTubeMiniPlayerState state,
        bool loadImmediately,
        Style buttonStyle,
        Func<YouTubeMiniPlayerState, WebView2> createWebView,
        Func<YouTubeMiniPlayerState, FrameworkElement> createPlaceholder,
        Func<YouTubeMiniPlayerState, LocalVideoPlayerView> createLocalVideoView,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        YouTubeMiniPlayerRuntimeWiringCallbacks wiringCallbacks,
        Action<Grid, YouTubeMiniPlayerState, FrameworkElement> addResizeThumbs)
    {
        state.IsShorts |= YouTubeUrlService.IsShortsUrl(state.Url);
        YouTubeMiniPlayerContentTransformState.Normalize(state);
        var fittedSize = YouTubeMiniPlayerLayoutService.NormalizeSizeToVideo(
            state.Width,
            state.Height,
            preferHeight: false,
            aspectRatio: YouTubeMiniPlayerLayoutService.GetVideoAspectRatio(state));
        state.Width = fittedSize.Width;
        state.Height = fittedSize.Height;

        WebView2? webView = null;
        LocalVideoPlayerView? localVideoView = null;
        UIElement webContent;
        if (state.SourceKind == MiniPlayerSourceKind.LocalFile)
        {
            localVideoView = createLocalVideoView(state);
            webContent = localVideoView;
        }
        else if (loadImmediately)
        {
            webView = createWebView(state);
            webContent = webView;
        }
        else
        {
            webContent = createPlaceholder(state);
        }

        var runtimeParts = YouTubeMiniPlayerRuntimeFactory.Create(
            state,
            buttonStyle,
            YouTubeMiniPlayerNavigationService.NormalizeTitle(state.Title, state.Url),
            webContent,
            webView,
            localVideoView);
        YouTubeMiniPlayerRuntimeWiringService.Attach(
            state,
            runtimeParts,
            runtimes,
            wiringCallbacks);
        addResizeThumbs(runtimeParts.Chrome, state, runtimeParts.Runtime.Root);
        return runtimeParts.Runtime;
    }
}
