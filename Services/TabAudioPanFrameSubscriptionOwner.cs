using System.Runtime.CompilerServices;
using Microsoft.Web.WebView2.Core;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal sealed class TabAudioPanFrameSubscriptionOwner
{
    private readonly ConditionalWeakTable<BrowserTab, List<FrameEventSubscription>> _subscriptions = new();
    private readonly Action<BrowserTab, CoreWebView2Frame> _queueInitialize;
    private readonly Action<BrowserTab, CoreWebView2Frame> _queueApply;
    private readonly Action<BrowserTab, CoreWebView2Frame> _registerChildFrame;

    public TabAudioPanFrameSubscriptionOwner(
        Action<BrowserTab, CoreWebView2Frame> queueInitialize,
        Action<BrowserTab, CoreWebView2Frame> queueApply,
        Action<BrowserTab, CoreWebView2Frame> registerChildFrame)
    {
        _queueInitialize = queueInitialize;
        _queueApply = queueApply;
        _registerChildFrame = registerChildFrame;
    }

    public bool TryRegister(BrowserTab tab, CoreWebView2Frame frame)
    {
        var subscriptions = _subscriptions.GetValue(tab, static _ => []);
        if (subscriptions.Any(subscription => ReferenceEquals(subscription.Frame, frame)))
        {
            return false;
        }

        EventHandler<object> destroyed = (_, _) =>
            Remove(tab, frame, detachHandlers: false);
        EventHandler<CoreWebView2ContentLoadingEventArgs> contentLoading = (_, _) =>
            _queueInitialize(tab, frame);
        EventHandler<CoreWebView2NavigationCompletedEventArgs> navigationCompleted = (_, _) =>
            _queueApply(tab, frame);
        EventHandler<CoreWebView2DOMContentLoadedEventArgs> domContentLoaded = (_, _) =>
            _queueApply(tab, frame);
        EventHandler<CoreWebView2FrameCreatedEventArgs> frameCreated = (_, args) =>
            _registerChildFrame(tab, args.Frame);
        var subscription = new FrameEventSubscription(
            frame,
            destroyed,
            contentLoading,
            navigationCompleted,
            domContentLoaded,
            frameCreated);
        subscription.Attach();
        subscriptions.Add(subscription);
        return true;
    }

    public IReadOnlyList<CoreWebView2Frame> CreateFrameSnapshot(BrowserTab tab)
    {
        return _subscriptions.TryGetValue(tab, out var subscriptions)
            ? subscriptions.Select(static subscription => subscription.Frame).ToArray()
            : Array.Empty<CoreWebView2Frame>();
    }

    public void Dispose(BrowserTab tab)
    {
        if (!_subscriptions.TryGetValue(tab, out var subscriptions))
        {
            return;
        }

        _subscriptions.Remove(tab);
        foreach (var subscription in subscriptions)
        {
            subscription.Dispose();
        }

        subscriptions.Clear();
    }

    private void Remove(
        BrowserTab tab,
        CoreWebView2Frame frame,
        bool detachHandlers = true)
    {
        if (!_subscriptions.TryGetValue(tab, out var subscriptions))
        {
            return;
        }

        var subscription = subscriptions.FirstOrDefault(
            current => ReferenceEquals(current.Frame, frame));
        if (subscription is null)
        {
            return;
        }

        subscriptions.Remove(subscription);
        if (detachHandlers)
        {
            subscription.Dispose();
        }

        if (subscriptions.Count == 0)
        {
            _subscriptions.Remove(tab);
        }
    }

    private sealed class FrameEventSubscription : IDisposable
    {
        private bool _isAttached;

        public FrameEventSubscription(
            CoreWebView2Frame frame,
            EventHandler<object> destroyed,
            EventHandler<CoreWebView2ContentLoadingEventArgs> contentLoading,
            EventHandler<CoreWebView2NavigationCompletedEventArgs> navigationCompleted,
            EventHandler<CoreWebView2DOMContentLoadedEventArgs> domContentLoaded,
            EventHandler<CoreWebView2FrameCreatedEventArgs> frameCreated)
        {
            Frame = frame;
            Destroyed = destroyed;
            ContentLoading = contentLoading;
            NavigationCompleted = navigationCompleted;
            DOMContentLoaded = domContentLoaded;
            FrameCreated = frameCreated;
        }

        public CoreWebView2Frame Frame { get; }

        private EventHandler<object> Destroyed { get; }

        private EventHandler<CoreWebView2ContentLoadingEventArgs> ContentLoading { get; }

        private EventHandler<CoreWebView2NavigationCompletedEventArgs> NavigationCompleted { get; }

        private EventHandler<CoreWebView2DOMContentLoadedEventArgs> DOMContentLoaded { get; }

        private EventHandler<CoreWebView2FrameCreatedEventArgs> FrameCreated { get; }

        public void Attach()
        {
            if (_isAttached)
            {
                return;
            }

            _isAttached = true;
            try
            {
                Frame.Destroyed += Destroyed;
                Frame.ContentLoading += ContentLoading;
                Frame.NavigationCompleted += NavigationCompleted;
                Frame.DOMContentLoaded += DOMContentLoaded;
                Frame.FrameCreated += FrameCreated;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (!_isAttached)
            {
                return;
            }

            _isAttached = false;
            TryDetach(() => Frame.Destroyed -= Destroyed);
            TryDetach(() => Frame.ContentLoading -= ContentLoading);
            TryDetach(() => Frame.NavigationCompleted -= NavigationCompleted);
            TryDetach(() => Frame.DOMContentLoaded -= DOMContentLoaded);
            TryDetach(() => Frame.FrameCreated -= FrameCreated);
        }

        private static void TryDetach(Action detach)
        {
            try
            {
                detach();
            }
            catch
            {
                // WebView2終了と競合したイベントはRuntime破棄に任せる。
            }
        }
    }
}
