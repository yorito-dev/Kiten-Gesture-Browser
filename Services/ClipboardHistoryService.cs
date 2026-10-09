using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using TabBrowser.Models;

namespace TabBrowser.Services;

public static class ClipboardHistoryContentFactory
{
    public const int MaximumTextBytes = 100 * 1024;
    public const int MaximumFileCount = 100;
    public const int MaximumFilePathCharacters = 100 * 1024;

    public static ClipboardHistoryContent? FromText(string? text, bool captureUrls)
    {
        if (string.IsNullOrWhiteSpace(text)
            || text.Length > MaximumTextBytes
            || Encoding.UTF8.GetByteCount(text) > MaximumTextBytes)
        {
            return null;
        }

        var trimmed = text.Trim();
        if (TryNormalizeHttpUrl(trimmed, out var url))
        {
            return captureUrls
                ? new ClipboardHistoryContent(ClipboardHistoryItemKind.Url, url, [])
                : null;
        }

        return new ClipboardHistoryContent(ClipboardHistoryItemKind.Text, text, []);
    }

    public static ClipboardHistoryContent? FromFiles(IEnumerable<string>? filePaths)
    {
        var normalizedPaths = new List<string>();
        var totalCharacters = 0;
        foreach (var filePath in filePaths ?? [])
        {
            if (normalizedPaths.Count >= MaximumFileCount)
            {
                break;
            }

            if (!TryNormalizeFilePath(filePath, out var normalizedPath))
            {
                continue;
            }

            if (normalizedPaths.Contains(normalizedPath, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            totalCharacters += normalizedPath.Length;
            if (totalCharacters > MaximumFilePathCharacters)
            {
                break;
            }

            normalizedPaths.Add(normalizedPath);
        }

        return normalizedPaths.Count == 0
            ? null
            : new ClipboardHistoryContent(ClipboardHistoryItemKind.Files, string.Empty, normalizedPaths);
    }

    private static bool TryNormalizeHttpUrl(string value, out string url)
    {
        url = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        url = uri.AbsoluteUri;
        return true;
    }

    private static bool TryNormalizeFilePath(string? value, out string path)
    {
        path = string.Empty;
        var candidate = value?.Trim();
        if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathFullyQualified(candidate))
        {
            return false;
        }

        try
        {
            path = Path.GetFullPath(candidate);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

public static class ClipboardHistoryContentReader
{
    public const string ExcludeFromMonitorProcessingFormat =
        "ExcludeClipboardContentFromMonitorProcessing";
    public const string CanIncludeInHistoryFormat = "CanIncludeInClipboardHistory";
    public const string CanUploadToCloudClipboardFormat = "CanUploadToCloudClipboard";
    public const string ClipboardViewerIgnoreFormat = "ClipboardViewerIgnore";

    public static ClipboardHistoryContent? Read(
        IDataObject? dataObject,
        ClipboardHistorySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (dataObject is null || ShouldExcludeFromHistory(dataObject))
        {
            return null;
        }

        if (dataObject.GetDataPresent(DataFormats.FileDrop, autoConvert: true))
        {
            if (!settings.CaptureFiles)
            {
                return null;
            }

            return ClipboardHistoryContentFactory.FromFiles(
                GetFilePaths(dataObject.GetData(DataFormats.FileDrop, autoConvert: true)));
        }

        if (!dataObject.GetDataPresent(DataFormats.UnicodeText, autoConvert: true)
            || dataObject.GetData(DataFormats.UnicodeText, autoConvert: true) is not string text)
        {
            return null;
        }

        return ClipboardHistoryContentFactory.FromText(text, settings.CaptureUrls);
    }

    private static bool ShouldExcludeFromHistory(IDataObject dataObject)
    {
        var formats = dataObject.GetFormats(autoConvert: false);
        if (ContainsFormat(formats, ExcludeFromMonitorProcessingFormat)
            || ContainsFormat(formats, ClipboardViewerIgnoreFormat))
        {
            return true;
        }

        var canIncludeFormat = FindFormat(formats, CanIncludeInHistoryFormat);
        if (canIncludeFormat is null)
        {
            return false;
        }

        try
        {
            return !TryReadDword(dataObject.GetData(canIncludeFormat, autoConvert: false), out var value)
                || value != 1;
        }
        catch (Exception ex) when (ex is ExternalException
            or IOException
            or NotSupportedException
            or ObjectDisposedException)
        {
            return true;
        }
    }

    private static IEnumerable<string> GetFilePaths(object? value)
    {
        return value switch
        {
            string[] paths => paths,
            IEnumerable<string> paths => paths,
            _ => []
        };
    }

    private static bool ContainsFormat(IEnumerable<string> formats, string expected)
    {
        return FindFormat(formats, expected) is not null;
    }

    private static string? FindFormat(IEnumerable<string> formats, string expected)
    {
        return formats.FirstOrDefault(format =>
            string.Equals(format, expected, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryReadDword(object? value, out uint result)
    {
        switch (value)
        {
            case uint unsigned:
                result = unsigned;
                return true;
            case int signed:
                result = unchecked((uint)signed);
                return true;
            case byte[] bytes when bytes.Length >= sizeof(uint):
                result = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
                return true;
            case ReadOnlyMemory<byte> memory when memory.Length >= sizeof(uint):
                result = BinaryPrimitives.ReadUInt32LittleEndian(memory.Span);
                return true;
            case Stream stream:
                return TryReadDword(stream, out result);
            default:
                result = 0;
                return false;
        }
    }

    private static bool TryReadDword(Stream stream, out uint result)
    {
        result = 0;
        var originalPosition = stream.CanSeek ? stream.Position : 0;
        try
        {
            if (stream.CanSeek)
            {
                stream.Position = 0;
            }

            Span<byte> buffer = stackalloc byte[sizeof(uint)];
            var totalRead = 0;
            while (totalRead < buffer.Length)
            {
                var read = stream.Read(buffer[totalRead..]);
                if (read == 0)
                {
                    return false;
                }

                totalRead += read;
            }

            result = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
            return true;
        }
        finally
        {
            if (stream.CanSeek)
            {
                stream.Position = originalPosition;
            }
        }
    }
}

public sealed class ClipboardHistoryStore
{
    private readonly ObservableCollection<ClipboardHistoryItem> _items = [];
    private int _maximumItemCount;

    public ClipboardHistoryStore(int maximumItemCount = ClipboardHistorySettingsProfile.DefaultMaximumItemCount)
    {
        _maximumItemCount = NormalizeMaximumItemCount(maximumItemCount);
        Items = new ReadOnlyObservableCollection<ClipboardHistoryItem>(_items);
    }

    public ReadOnlyObservableCollection<ClipboardHistoryItem> Items { get; }

    public ClipboardHistoryItem Add(ClipboardHistoryContent content, DateTimeOffset capturedAt)
    {
        ArgumentNullException.ThrowIfNull(content);

        var existing = _items.FirstOrDefault(item => HasSameContent(item, content));
        var item = existing is null
            ? new ClipboardHistoryItem(Guid.NewGuid(), content.Kind, content.Text, content.FilePaths.ToList(), capturedAt)
            : existing with { CapturedAt = capturedAt };
        if (existing is not null)
        {
            _items.Remove(existing);
        }

        _items.Insert(0, item);
        TrimToLimit();
        return item;
    }

    public bool Remove(Guid itemId)
    {
        var item = _items.FirstOrDefault(candidate => candidate.Id == itemId);
        return item is not null && _items.Remove(item);
    }

    public void Clear()
    {
        _items.Clear();
    }

    public void UpdateMaximumItemCount(int maximumItemCount)
    {
        _maximumItemCount = NormalizeMaximumItemCount(maximumItemCount);
        TrimToLimit();
    }

    private static bool HasSameContent(ClipboardHistoryItem item, ClipboardHistoryContent content)
    {
        if (item.Kind != content.Kind)
        {
            return false;
        }

        return item.Kind == ClipboardHistoryItemKind.Files
            ? item.FilePaths.SequenceEqual(content.FilePaths, StringComparer.OrdinalIgnoreCase)
            : string.Equals(item.Text, content.Text, StringComparison.Ordinal);
    }

    private static int NormalizeMaximumItemCount(int value)
    {
        return Math.Clamp(
            value,
            ClipboardHistorySettingsProfile.MinimumMaximumItemCount,
            ClipboardHistorySettingsProfile.MaximumMaximumItemCount);
    }

    private void TrimToLimit()
    {
        while (_items.Count > _maximumItemCount)
        {
            _items.RemoveAt(_items.Count - 1);
        }
    }
}

public sealed record ClipboardHistoryOperationResult(bool IsSuccess, string Message)
{
    public static ClipboardHistoryOperationResult Success(string message) => new(true, message);

    public static ClipboardHistoryOperationResult Failure(string message) => new(false, message);
}

public sealed class ClipboardHistoryService : IDisposable
{
    private const int ClipboardUpdateMessage = 0x031D;
    private static readonly TimeSpan[] ClipboardRetryDelays =
    [
        TimeSpan.Zero,
        TimeSpan.FromMilliseconds(40),
        TimeSpan.FromMilliseconds(120)
    ];

    private readonly ClipboardHistoryStore _store = new();
    private ClipboardHistorySettings _settings = new();
    private HwndSource? _messageSource;
    private CancellationTokenSource? _captureCancellation;
    private uint _lastClipboardSequenceNumber;
    private bool _listenerRegistered;
    private bool _isPaused;
    private bool _isDisposed;

    public event EventHandler? StateChanged;

    public event NotifyCollectionChangedEventHandler? ItemsChanged
    {
        add => ((INotifyCollectionChanged)_store.Items).CollectionChanged += value;
        remove => ((INotifyCollectionChanged)_store.Items).CollectionChanged -= value;
    }

    public ReadOnlyObservableCollection<ClipboardHistoryItem> Items => _store.Items;

    public bool IsEnabled => _settings.IsEnabled;

    public bool IsMonitoring => _listenerRegistered;

    public bool IsPaused => _isPaused;

    public string? LastError { get; private set; }

    public void ApplySettings(ClipboardHistorySettings? settings)
    {
        var normalized = ClipboardHistorySettingsProfile.Normalize(settings);
        RunOnApplicationDispatcher(() => ApplySettingsCore(normalized));
    }

    public void SetPaused(bool paused)
    {
        RunOnApplicationDispatcher(() =>
        {
            if (!_settings.IsEnabled || _isPaused == paused)
            {
                return;
            }

            _isPaused = paused;
            _captureCancellation?.Cancel();
            _lastClipboardSequenceNumber = GetClipboardSequenceNumber();
            StateChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    public void Clear()
    {
        RunOnApplicationDispatcher(_store.Clear);
    }

    public bool Remove(Guid itemId)
    {
        var removed = false;
        RunOnApplicationDispatcher(() => removed = _store.Remove(itemId));
        return removed;
    }

    public ClipboardHistoryOperationResult CopyToClipboard(ClipboardHistoryItem? item)
    {
        if (item is null)
        {
            return ClipboardHistoryOperationResult.Failure("コピーする履歴を選択してください。");
        }

        try
        {
            if (item.Kind == ClipboardHistoryItemKind.Files)
            {
                var paths = new StringCollection();
                paths.AddRange(item.FilePaths.ToArray());
                Clipboard.SetFileDropList(paths);
            }
            else
            {
                Clipboard.SetText(item.Text);
            }

            return ClipboardHistoryOperationResult.Success("クリップボードへコピーしました。");
        }
        catch (Exception ex) when (ex is ExternalException or ThreadStateException or ArgumentException)
        {
            return ClipboardHistoryOperationResult.Failure("クリップボードへコピーできませんでした。少し待ってから再試行してください。");
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        RunOnApplicationDispatcher(() =>
        {
            StopMonitoring();
            _store.Clear();
            StateChanged = null;
        });
    }

    private void ApplySettingsCore(ClipboardHistorySettings settings)
    {
        if (_isDisposed)
        {
            return;
        }

        _settings = settings;
        _store.UpdateMaximumItemCount(settings.MaximumItemCount);
        if (settings.IsEnabled)
        {
            StartMonitoring();
        }
        else
        {
            _isPaused = false;
            StopMonitoring();
            _store.Clear();
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void StartMonitoring()
    {
        if (_listenerRegistered || _isDisposed)
        {
            return;
        }

        LastError = null;
        var source = new HwndSource(new HwndSourceParameters("TabBrowser.ClipboardHistory")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0
        });
        source.AddHook(MessageHook);
        if (!AddClipboardFormatListener(source.Handle))
        {
            source.RemoveHook(MessageHook);
            source.Dispose();
            LastError = "Windowsのクリップボード変更通知を開始できませんでした。";
            return;
        }

        _messageSource = source;
        _listenerRegistered = true;
        _lastClipboardSequenceNumber = GetClipboardSequenceNumber();
    }

    private void StopMonitoring()
    {
        _captureCancellation?.Cancel();
        _captureCancellation = null;

        if (_messageSource is { } source)
        {
            if (_listenerRegistered)
            {
                _ = RemoveClipboardFormatListener(source.Handle);
            }

            source.RemoveHook(MessageHook);
            source.Dispose();
        }

        _messageSource = null;
        _listenerRegistered = false;
        _lastClipboardSequenceNumber = 0;
    }

    private IntPtr MessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == ClipboardUpdateMessage && _settings.IsEnabled && !_isPaused)
        {
            var sequenceNumber = GetClipboardSequenceNumber();
            if (sequenceNumber == 0 || sequenceNumber != _lastClipboardSequenceNumber)
            {
                _lastClipboardSequenceNumber = sequenceNumber;
                QueueClipboardCapture(sequenceNumber);
            }
        }

        return IntPtr.Zero;
    }

    private void QueueClipboardCapture(uint sequenceNumber)
    {
        _captureCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _captureCancellation = cancellation;
        _ = CaptureClipboardAsync(sequenceNumber, cancellation);
    }

    private async Task CaptureClipboardAsync(uint sequenceNumber, CancellationTokenSource cancellation)
    {
        try
        {
            for (var attempt = 0; attempt < ClipboardRetryDelays.Length; attempt++)
            {
                var delay = ClipboardRetryDelays[attempt];
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellation.Token);
                }

                cancellation.Token.ThrowIfCancellationRequested();
                try
                {
                    var content = ReadClipboardContent();
                    if (sequenceNumber != 0 && GetClipboardSequenceNumber() != sequenceNumber)
                    {
                        return;
                    }

                    if (content is not null)
                    {
                        _store.Add(content, DateTimeOffset.Now);
                    }

                    if (LastError is not null)
                    {
                        LastError = null;
                        StateChanged?.Invoke(this, EventArgs.Empty);
                    }

                    return;
                }
                catch (ExternalException) when (attempt + 1 < ClipboardRetryDelays.Length)
                {
                }
            }

            LastError = "クリップボードを読み取れませんでした。次の更新を待機します。";
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            LastError = "クリップボード履歴の更新に失敗しました。次の更新を待機します。";
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            if (ReferenceEquals(_captureCancellation, cancellation))
            {
                _captureCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private ClipboardHistoryContent? ReadClipboardContent()
    {
        return ClipboardHistoryContentReader.Read(Clipboard.GetDataObject(), _settings);
    }

    private static void RunOnApplicationDispatcher(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(action);
            return;
        }

        action();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
