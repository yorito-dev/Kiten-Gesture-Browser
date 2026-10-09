using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TabBrowser.Services;

// 配布用のシークプレビューもWPF標準の再生機能で用意する。
internal sealed class LocalVideoFramePreviewService : IDisposable
{
    private readonly string _filePath;
    private readonly Dispatcher _dispatcher;
    private readonly MediaPlayer _player = new() { Volume = 0, IsMuted = true, ScrubbingEnabled = true };
    private readonly TaskCompletionSource<bool> _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _request;
    private bool _isOpening;
    private bool _isDisposed;

    public LocalVideoFramePreviewService(string filePath, Dispatcher dispatcher)
    {
        _filePath = filePath;
        _dispatcher = dispatcher;
        _dispatcher.VerifyAccess();
        _player.MediaOpened += Player_Opened;
        _player.MediaFailed += Player_Failed;
    }

    public async Task<BitmapSource?> GetFrameAsync(double positionSeconds, CancellationToken cancellationToken)
    {
        _dispatcher.VerifyAccess();
        if (_isDisposed || !double.IsFinite(positionSeconds) || cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        _request?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        _request = request;
        try
        {
            if (!_isOpening)
            {
                _isOpening = true;
                _player.Open(new Uri(Path.GetFullPath(_filePath)));
                _player.Pause();
            }

            if (!await _opened.Task.WaitAsync(TimeSpan.FromSeconds(5), request.Token))
            {
                return null;
            }

            await _dispatcher.InvokeAsync(() =>
            {
                request.Token.ThrowIfCancellationRequested();
                var duration = _player.NaturalDuration;
                var position = Math.Max(0, positionSeconds);
                if (duration.HasTimeSpan)
                {
                    position = Math.Min(position, Math.Max(0, duration.TimeSpan.TotalSeconds - 0.1));
                }
                _player.Position = TimeSpan.FromSeconds(position);
                _player.Pause();
            });

            // WPFにはフレーム到着通知がないため、シーク後の描画を有限時間だけ待つ。
            await Task.Delay(150, request.Token);
            return await _dispatcher.InvokeAsync(() =>
            {
                request.Token.ThrowIfCancellationRequested();
                if (_isDisposed || !_player.HasVideo || _player.NaturalVideoWidth <= 0 || _player.NaturalVideoHeight <= 0)
                {
                    return null;
                }

                const int width = 192;
                const int height = 108;
                var ratio = Math.Min((double)width / _player.NaturalVideoWidth, (double)height / _player.NaturalVideoHeight);
                var videoWidth = _player.NaturalVideoWidth * ratio;
                var videoHeight = _player.NaturalVideoHeight * ratio;
                var drawing = new VideoDrawing
                {
                    Player = _player,
                    Rect = new Rect((width - videoWidth) / 2, (height - videoHeight) / 2, videoWidth, videoHeight)
                };
                var visual = new DrawingVisual();
                using (var context = visual.RenderOpen())
                {
                    context.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));
                    context.DrawDrawing(drawing);
                }
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                bitmap.Freeze();
                return (BitmapSource?)bitmap;
            });
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
        finally
        {
            if (ReferenceEquals(_request, request))
            {
                _request = null;
            }
        }
    }

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _lifetime.Cancel();
        _player.MediaOpened -= Player_Opened;
        _player.MediaFailed -= Player_Failed;
        _player.Close();
        _lifetime.Dispose();
    }

    private void Player_Opened(object? sender, EventArgs e)
    {
        _player.Pause();
        _opened.TrySetResult(true);
    }

    private void Player_Failed(object? sender, ExceptionEventArgs e)
    {
        _opened.TrySetResult(false);
    }
}
