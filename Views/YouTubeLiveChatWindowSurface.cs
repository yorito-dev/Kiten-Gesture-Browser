using System.Windows;

namespace TabBrowser.Views;

internal interface IYouTubeLiveChatWindowSurface
{
    YouTubeLiveChatWindow? NativeWindow { get; }
    bool IsActive { get; }
    bool IsLoaded { get; }
    event EventHandler? Closed;
    event EventHandler? LayoutChanged;
    void Show();
    void Close();
    void BringToFront();
    void FollowSourceToFront();
    void BeginVideoTransition(string videoId);
    void ShowUnavailable(string videoId, string message = "この動画ではチャットを利用できません");
    Task InitializeAsync();
    Task SwitchVideoAsync(string videoId, Uri uri);
    void SynchronizeWithSourceWindow();
    void ExitFullScreenOverlay();
    void EnterFullScreenOverlay(Rect bounds, bool topmost);
    void UpdateFullScreenSourceBounds(Rect bounds, bool topmost);
}

// 実Windowとの接続だけを担当し、動画とRuntimeの現行性はControllerが確認する。
internal sealed class YouTubeLiveChatWindowSurface : IYouTubeLiveChatWindowSurface
{
    private readonly YouTubeLiveChatWindow _window;

    public YouTubeLiveChatWindowSurface(YouTubeLiveChatWindow window)
    {
        _window = window;
        _window.Closed += Window_Closed;
        _window.LocationChanged += Window_LayoutChanged;
        _window.SizeChanged += Window_SizeChanged;
        _window.StateChanged += Window_LayoutChanged;
    }

    public YouTubeLiveChatWindow NativeWindow => _window;
    public bool IsActive => _window.IsActive;
    public bool IsLoaded => _window.IsLoaded;
    public event EventHandler? Closed;
    public event EventHandler? LayoutChanged;
    public void Show() => _window.Show();
    public void Close() => _window.Close();
    public void BringToFront() => _window.BringToFront();
    public void FollowSourceToFront() => _window.FollowSourceToFront();
    public void BeginVideoTransition(string videoId) => _window.BeginVideoTransition(videoId);
    public void ShowUnavailable(string videoId, string message = "この動画ではチャットを利用できません")
        => _window.ShowUnavailable(videoId, message);
    public Task InitializeAsync() => _window.InitializeAsync();
    public Task SwitchVideoAsync(string videoId, Uri uri) => _window.SwitchVideoAsync(videoId, uri);
    public void SynchronizeWithSourceWindow() => _window.SynchronizeWithSourceWindow();
    public void ExitFullScreenOverlay() => _window.ExitFullScreenOverlay();
    public void EnterFullScreenOverlay(Rect bounds, bool topmost) => _window.EnterFullScreenOverlay(bounds, topmost);
    public void UpdateFullScreenSourceBounds(Rect bounds, bool topmost) => _window.UpdateFullScreenSourceBounds(bounds, topmost);

    private void Window_LayoutChanged(object? sender, EventArgs e) => LayoutChanged?.Invoke(this, e);
    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutChanged?.Invoke(this, e);

    private void Window_Closed(object? sender, EventArgs e)
    {
        try
        {
            Closed?.Invoke(this, e);
        }
        finally
        {
            _window.Closed -= Window_Closed;
            _window.LocationChanged -= Window_LayoutChanged;
            _window.SizeChanged -= Window_SizeChanged;
            _window.StateChanged -= Window_LayoutChanged;
            Closed = null;
            LayoutChanged = null;
        }
    }
}
