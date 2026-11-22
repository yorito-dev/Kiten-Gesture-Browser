namespace TabBrowser.Services;

internal sealed class YouTubeMiniPlayerLiveChatState
{
    public bool? Availability { get; private set; }
    public bool IsWindowOpen { get; private set; }

    private string? _videoId;
    private object? _document;
    private int _generation;
    private int _attempts;
    private Task<YouTubeLiveChatAvailabilityResult?>? _pending;

    public void SetAvailability(bool? availability) => Availability = availability;

    public void SetWindowOpen(bool isOpen) => IsWindowOpen = isOpen;

    public void ResetAvailabilityRequests()
    {
        _generation++;
        _videoId = null;
        _document = null;
        _attempts = 0;
        _pending = null;
    }

    // NavigationCompleted／canplay／playingが重なる間の同一問い合わせを共有する。所有UIスレッドで使う。
    public Task<YouTubeLiveChatAvailabilityResult?> ReadAvailabilityAsync(string videoId, object document,
        Func<Func<bool>, Task<YouTubeLiveChatAvailabilityResult?>> read)
    {
        if (_videoId != videoId || !ReferenceEquals(_document, document))
        {
            ResetAvailabilityRequests();
            _videoId = videoId;
            _document = document;
        }
        if (_pending is not null) return _pending;
        if (_attempts >= 2) return Task.FromResult<YouTubeLiveChatAvailabilityResult?>(null);

        var completion = new TaskCompletionSource<YouTubeLiveChatAvailabilityResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var generation = _generation;
        _pending = completion.Task;
        _ = CompleteAsync();
        return completion.Task;

        async Task CompleteAsync()
        {
            YouTubeLiveChatAvailabilityResult? result = null;
            Exception? failure = null;
            try
            {
                result = await read(() => generation == _generation);
                if (generation != _generation) result = null;
                else if (result.HasValue) _attempts++;
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                if (generation == _generation && ReferenceEquals(_pending, completion.Task)) _pending = null;
            }
            if (failure is not null) completion.TrySetException(failure);
            else completion.TrySetResult(result);
        }
    }
}
