using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal readonly record struct TabRuntimeTransientSnapshot(
    bool IsLoaded,
    bool IsLoading,
    bool IsSuspended,
    bool IsMemoryReleased,
    bool IsInitializing,
    long InitializationGeneration,
    long PendingOperationGeneration,
    ImageSource? Thumbnail,
    ImageSource? Favicon);

internal sealed class TabRuntimeTransientState : ITabRuntimePresentation
{
    private readonly object _gate = new();
    private bool _isLoaded;
    private bool _isLoading;
    private bool _isSuspended;
    private bool _isMemoryReleased;
    private bool _isInitializing;
    private bool _isTerminated;
    private long _initializationGeneration;
    private long _pendingOperationGeneration;
    private TaskCompletionSource<bool>? _initializationCompletion;
    private ImageSource? _thumbnail;
    private ImageSource? _favicon;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsLoaded => Read(static state => state._isLoaded);

    public bool IsLoading => Read(static state => state._isLoading);

    public bool IsSuspended => Read(static state => state._isSuspended);

    public bool IsMemoryReleased => Read(static state => state._isMemoryReleased);

    public bool IsInitializing => Read(static state => state._isInitializing);

    public ImageSource? Thumbnail => Read(static state => state._thumbnail);

    public ImageSource? Favicon => Read(static state => state._favicon);

    public bool SetIsLoaded(bool value)
    {
        return SetValue(ref _isLoaded, value, nameof(IsLoaded));
    }

    public bool SetIsLoading(bool value)
    {
        return SetValue(ref _isLoading, value, nameof(IsLoading));
    }

    public bool SetIsSuspended(bool value)
    {
        return SetValue(ref _isSuspended, value, nameof(IsSuspended));
    }

    public bool SetIsMemoryReleased(bool value)
    {
        return SetValue(ref _isMemoryReleased, value, nameof(IsMemoryReleased));
    }

    public bool SetThumbnail(ImageSource? value)
    {
        lock (_gate)
        {
            if (_isTerminated || ReferenceEquals(_thumbnail, value))
            {
                return false;
            }

            _thumbnail = value;
        }

        OnPropertyChanged(nameof(Thumbnail));
        return true;
    }

    public bool SetFavicon(ImageSource? value)
    {
        lock (_gate)
        {
            if (_isTerminated || ReferenceEquals(_favicon, value))
            {
                return false;
            }

            _favicon = value;
        }

        OnPropertyChanged(nameof(Favicon));
        return true;
    }

    public bool TryBeginInitialization(out long generation)
    {
        lock (_gate)
        {
            if (_isTerminated || _isInitializing)
            {
                generation = _initializationGeneration;
                return false;
            }

            generation = ++_initializationGeneration;
            _isInitializing = true;
            _initializationCompletion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            return true;
        }
    }

    public bool IsInitializationCurrent(long generation)
    {
        lock (_gate)
        {
            return !_isTerminated
                && _isInitializing
                && _initializationGeneration == generation;
        }
    }

    public bool EndInitialization(long generation)
    {
        TaskCompletionSource<bool>? completion;
        lock (_gate)
        {
            if (_isTerminated
                || !_isInitializing
                || _initializationGeneration != generation)
            {
                return false;
            }

            _isInitializing = false;
            completion = _initializationCompletion;
            _initializationCompletion = null;
        }

        completion?.TrySetResult(true);
        return true;
    }

    public void CancelInitialization()
    {
        TaskCompletionSource<bool>? completion;
        lock (_gate)
        {
            if (_isTerminated)
            {
                return;
            }

            _initializationGeneration++;
            _isInitializing = false;
            completion = _initializationCompletion;
            _initializationCompletion = null;
        }

        completion?.TrySetResult(true);
    }

    public Task WaitForInitializationAsync()
    {
        lock (_gate)
        {
            return _initializationCompletion?.Task ?? Task.CompletedTask;
        }
    }

    public long CapturePendingOperationGeneration()
    {
        lock (_gate)
        {
            return _pendingOperationGeneration;
        }
    }

    public bool IsPendingOperationCurrent(long generation)
    {
        lock (_gate)
        {
            return !_isTerminated && _pendingOperationGeneration == generation;
        }
    }

    public void InvalidatePendingOperations()
    {
        lock (_gate)
        {
            if (!_isTerminated)
            {
                _pendingOperationGeneration++;
            }
        }
    }

    public TabRuntimeTransientSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return new TabRuntimeTransientSnapshot(
                _isLoaded,
                _isLoading,
                _isSuspended,
                _isMemoryReleased,
                _isInitializing,
                _initializationGeneration,
                _pendingOperationGeneration,
                _thumbnail,
                _favicon);
        }
    }

    public void ClearForTermination()
    {
        TaskCompletionSource<bool>? completion;
        bool notifyIsLoaded;
        bool notifyIsLoading;
        bool notifyIsSuspended;
        bool notifyIsMemoryReleased;
        bool notifyThumbnail;
        bool notifyFavicon;
        lock (_gate)
        {
            if (_isTerminated)
            {
                return;
            }

            _isTerminated = true;
            notifyIsLoaded = _isLoaded;
            notifyIsLoading = _isLoading;
            notifyIsSuspended = _isSuspended;
            notifyIsMemoryReleased = _isMemoryReleased;
            notifyThumbnail = _thumbnail is not null;
            notifyFavicon = _favicon is not null;
            _isLoaded = false;
            _isLoading = false;
            _isSuspended = false;
            _isMemoryReleased = false;
            _isInitializing = false;
            _initializationGeneration++;
            _pendingOperationGeneration++;
            _thumbnail = null;
            _favicon = null;
            completion = _initializationCompletion;
            _initializationCompletion = null;
        }

        completion?.TrySetResult(true);
        NotifyIfChanged(notifyIsLoaded, nameof(IsLoaded));
        NotifyIfChanged(notifyIsLoading, nameof(IsLoading));
        NotifyIfChanged(notifyIsSuspended, nameof(IsSuspended));
        NotifyIfChanged(notifyIsMemoryReleased, nameof(IsMemoryReleased));
        NotifyIfChanged(notifyThumbnail, nameof(Thumbnail));
        NotifyIfChanged(notifyFavicon, nameof(Favicon));
    }

    private T Read<T>(Func<TabRuntimeTransientState, T> read)
    {
        lock (_gate)
        {
            return read(this);
        }
    }

    private bool SetValue(ref bool field, bool value, string propertyName)
    {
        lock (_gate)
        {
            if (_isTerminated || field == value)
            {
                return false;
            }

            field = value;
        }

        OnPropertyChanged(propertyName);
        return true;
    }

    private void NotifyIfChanged(bool changed, string propertyName)
    {
        if (changed)
        {
            OnPropertyChanged(propertyName);
        }
    }

    private void OnPropertyChanged(
        string propertyName,
        [CallerMemberName] string? caller = null)
    {
        _ = caller;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
