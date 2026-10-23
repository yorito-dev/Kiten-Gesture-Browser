using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using TabBrowser.Models;
using TabBrowser.Views;
using Xunit;

namespace TabBrowser.Tests;

public sealed class UrlThumbnailCaptureControllerTests
{
    [Fact]
    public void PreviewViewUsesDedicatedInPrivateProfileBeforeInitialization()
    {
        RunInSta(async () =>
        {
            using var fixture = new Fixture();
            var capture = fixture.Controller.CaptureLinkPreviewAsync(Fixture.Url, "link", default);
            var view = Assert.IsType<WebView2>(Assert.Single(fixture.Panel.Children));
            try
            {
                Assert.NotNull(view.CreationProperties);
                Assert.True(view.CreationProperties.IsInPrivateModeEnabled);
                Assert.StartsWith("KitenPreview-", view.CreationProperties.ProfileName);
                Assert.Null(view.CoreWebView2);
            }
            finally
            {
                fixture.Host.Environment.TrySetException(new InvalidOperationException("fixture-only"));
                await capture;
            }
        });
    }

    [Fact]
    public void LinkPaletteAndWorkspaceShareCacheWithoutCreatingWebView()
    {
        RunInSta(async () =>
        {
            using var fixture = new Fixture();
            Assert.False(fixture.Controller.HasWebView);
            var image = new DrawingImage();
            fixture.Controller.StoreCached(Fixture.Url, image);
            Assert.Same(image, await fixture.Controller.CaptureLinkPreviewAsync(Fixture.Url, "link", default));
            var item = fixture.CreateItem();
            await fixture.Controller.CaptureItemAsync(item);
            Assert.Same(image, item.Thumbnail);
            Assert.Same(image, await fixture.Controller.CaptureWorkspaceAsync(Fixture.Url, "workspace"));
            Assert.Equal(0, fixture.Host.EnvironmentCount);
            Assert.Empty(fixture.Panel.Children);
            fixture.Controller.ResetWebView();
            Assert.True(fixture.Controller.TryGetCached(Fixture.Url, out var cached));
            Assert.Same(image, cached);
        });
    }

    [Fact]
    public void InvalidUrlUsesFallbackAndUnsupportedOrLoadedItemDoesNoWork()
    {
        RunInSta(async () =>
        {
            using var fixture = new Fixture();
            var invalid = fixture.CreateItem("javascript:alert(1)");
            await fixture.Controller.CaptureItemAsync(invalid);
            Assert.Same(fixture.Host.Fallback, invalid.Thumbnail);
            foreach (var type in new[] { CommandPaletteItemType.Command, CommandPaletteItemType.Tab })
            {
                var item = new CommandPaletteItem { Type = type, Url = Fixture.Url };
                await fixture.Controller.CaptureItemAsync(item);
                Assert.Null(item.Thumbnail);
            }

            var loaded = fixture.CreateItem();
            loaded.Thumbnail = new DrawingImage();
            var existing = loaded.Thumbnail;
            await fixture.Controller.CaptureItemAsync(loaded);
            Assert.Same(existing, loaded.Thumbnail);
            Assert.Equal(0, fixture.Host.EnvironmentCount);
            Assert.False(fixture.Controller.HasWebView);
        });
    }

    [Fact]
    public void ClosingAndPreCancelledRequestsKeepTheirDifferentFallbackRules()
    {
        RunInSta(async () =>
        {
            using var fixture = new Fixture();
            fixture.Host.IsClosingWindow = true;
            var item = fixture.CreateItem();
            await fixture.Controller.CaptureItemAsync(item);
            Assert.Null(item.Thumbnail);
            Assert.Same(fixture.Host.Fallback,
                await fixture.Controller.CaptureLinkPreviewAsync(Fixture.Url, "title", default));
            fixture.Host.IsClosingWindow = false;
            fixture.Lifetime.Cancel();
            await fixture.Controller.CaptureItemAsync(item);
            Assert.Null(item.Thumbnail);
            Assert.Same(fixture.Host.Fallback,
                await fixture.Controller.CaptureLinkPreviewAsync(Fixture.Url, "title", fixture.Lifetime.Token));
            Assert.Equal(0, fixture.Host.EnvironmentCount);
            Assert.False(fixture.Controller.HasWebView);
        });
    }

    [Fact]
    public void DuplicateItemsShareCaptureAndFailureDoesNotCacheFallback()
    {
        RunInSta(async () =>
        {
            using var fixture = new Fixture();
            var first = fixture.CreateItem();
            var second = fixture.CreateItem();
            var capture = fixture.Controller.CaptureItemAsync(first);
            await fixture.Controller.CaptureItemAsync(second);
            Assert.Equal(1, fixture.Host.EnvironmentCount);
            Assert.Null(second.Thumbnail);
            Assert.Same(fixture.Host.Fallback,
                await fixture.Controller.CaptureLinkPreviewAsync(Fixture.Url, "link", default));
            Assert.Equal(1, fixture.Host.EnvironmentCount);

            fixture.Host.Environment.SetException(new InvalidOperationException("テスト用の環境取得失敗"));
            await capture;
            Assert.Same(fixture.Host.Fallback, first.Thumbnail);
            Assert.Same(fixture.Host.Fallback, second.Thumbnail);
            Assert.False(fixture.Controller.TryGetCached(Fixture.Url, out _));

            // claimとpermitが解放されていれば同じURLを再試行できる。
            await fixture.Controller.CaptureItemAsync(fixture.CreateItem());
            Assert.Equal(2, fixture.Host.EnvironmentCount);
        });
    }

    [Fact]
    public void DifferentUrlsSerializeAccessToTheSameHiddenWebView()
    {
        RunInSta(async () =>
        {
            using var fixture = new Fixture();
            var first = fixture.Controller.CaptureItemAsync(fixture.CreateItem());
            var second = fixture.Controller.CaptureItemAsync(fixture.CreateItem("https://example.org/second"));
            Assert.Equal(1, fixture.Host.EnvironmentCount);
            var webView = Assert.IsType<WebView2>(Assert.Single(fixture.Panel.Children.Cast<object>()));
            Assert.Equal(1024, webView.Width);
            Assert.Equal(576, webView.Height);
            Assert.Equal(-32000, webView.Margin.Left);
            Assert.False(webView.Focusable);
            Assert.False(webView.IsHitTestVisible);
            Assert.Null(webView.CoreWebView2);
            fixture.Host.Environment.SetException(new InvalidOperationException("テスト用の環境取得失敗"));
            await Task.WhenAll(first, second);
            Assert.Equal(2, fixture.Host.EnvironmentCount);
            Assert.Same(webView, Assert.Single(fixture.Panel.Children.Cast<object>()));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResetDuringEnvironmentWaitRejectsOldWebViewAndAllowsRecreation(bool linkPreview)
    {
        RunInSta(async () =>
        {
            using var fixture = new Fixture();
            var item = fixture.CreateItem();
            var capture = linkPreview
                ? (Task)fixture.Controller.CaptureLinkPreviewAsync(Fixture.Url, "title", default)
                : fixture.Controller.CaptureItemAsync(item);
            var oldWebView = Assert.Single(fixture.Panel.Children.Cast<object>());
            fixture.Controller.ResetWebView();
            Assert.False(fixture.Controller.HasWebView);
            Assert.Empty(fixture.Panel.Children);
            // 参照同一性の検査で戻るため、実Coreの初期化へは到達しない。
            fixture.Host.Environment.SetResult(null!);
            await capture;
            if (linkPreview)
            {
                Assert.Same(fixture.Host.Fallback, await (Task<ImageSource>)capture);
            }
            else
            {
                Assert.Null(item.Thumbnail);
            }

            fixture.Host.Environment = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var next = fixture.Controller.CaptureItemAsync(fixture.CreateItem());
            Assert.NotSame(oldWebView, Assert.Single(fixture.Panel.Children.Cast<object>()));
            fixture.Lifetime.Cancel();
            fixture.Host.Environment.SetResult(null!);
            await next;
            Assert.Equal(2, fixture.Host.EnvironmentCount);
        });
    }

    private static void RunInSta(Func<Task> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await action();
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "サムネイル取得のSTAテストが期限内に完了しませんでした。");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal const string Url = "https://example.org/preview";
        internal readonly Grid Panel = new();
        internal readonly Host Host = new();
        internal readonly CancellationTokenSource Lifetime = new();
        internal readonly UrlThumbnailCaptureController Controller;

        internal Fixture()
        {
            Controller = new UrlThumbnailCaptureController(Panel, -32000, Host, Lifetime.Token);
        }

        internal CommandPaletteItem CreateItem(string url = Url)
            => new() { Type = CommandPaletteItemType.History, Title = "title", Url = url };

        public void Dispose()
        {
            Lifetime.Cancel();
            Controller.ResetWebView();
            Lifetime.Dispose();
        }
    }

    private sealed class Host : IUrlThumbnailCaptureHost
    {
        internal readonly ImageSource Fallback = new DrawingImage();
        internal int EnvironmentCount;
        internal TaskCompletionSource<CoreWebView2Environment> Environment = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsClosingWindow { get; set; }

        public Task<CoreWebView2Environment> EnsureEnvironmentAsync()
        {
            EnvironmentCount++;
            return Environment.Task;
        }

        public ImageSource CreateFallbackThumbnail(string title, string? url) => Fallback;
    }
}
