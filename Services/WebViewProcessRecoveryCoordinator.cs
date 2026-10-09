using Microsoft.Web.WebView2.Core;
using TabBrowser.Models;

namespace TabBrowser.Services;

public sealed record WebViewBrowserProcessExit(
    string ContainerProfileId,
    uint BrowserProcessId,
    CoreWebView2BrowserProcessExitKind ExitKind,
    DateTimeOffset OccurredAt);

public sealed record WebViewProcessRecoveryDiagnosticEntry(
    DateTimeOffset OccurredAt,
    string Scope,
    uint? BrowserProcessId,
    string FailureKind,
    string Action,
    string Result);

public sealed class WebViewProcessRecoveryCoordinator
{
    public const int DefaultCapacity = 64;

    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly Dictionary<Guid, Action<WebViewBrowserProcessExit>> _windowCallbacks = [];
    private readonly HashSet<string> _recentExitKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _recentExitOrder = new();
    private readonly Queue<WebViewProcessRecoveryDiagnosticEntry> _diagnostics = new();

    public WebViewProcessRecoveryCoordinator(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    public void RegisterWindow(Guid windowId, Action<WebViewBrowserProcessExit> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_gate)
        {
            _windowCallbacks[windowId] = callback;
        }
    }

    public void UnregisterWindow(Guid windowId)
    {
        lock (_gate)
        {
            _windowCallbacks.Remove(windowId);
        }
    }

    public bool NotifyBrowserProcessExited(WebViewBrowserProcessExit notification)
    {
        if (notification.ExitKind != CoreWebView2BrowserProcessExitKind.Failed)
        {
            return false;
        }

        Action<WebViewBrowserProcessExit>[] callbacks;
        lock (_gate)
        {
            var key = $"{ContainerProfile.NormalizeId(notification.ContainerProfileId)}:{notification.BrowserProcessId}:{notification.ExitKind}";
            if (!_recentExitKeys.Add(key))
            {
                return false;
            }

            _recentExitOrder.Enqueue(key);
            while (_recentExitOrder.Count > _capacity)
            {
                _recentExitKeys.Remove(_recentExitOrder.Dequeue());
            }

            AddDiagnosticCore(new WebViewProcessRecoveryDiagnosticEntry(
                notification.OccurredAt,
                GetDiagnosticScope(notification.ContainerProfileId),
                notification.BrowserProcessId,
                CoreWebView2ProcessFailedKind.BrowserProcessExited.ToString(),
                WebViewProcessFailureAction.RecreateProcessGroup.ToString(),
                "queued"));
            callbacks = _windowCallbacks.Values.ToArray();
        }

        foreach (var callback in callbacks)
        {
            try
            {
                callback(notification);
            }
            catch
            {
                RecordRecoveryResult(
                    notification.ContainerProfileId,
                    notification.BrowserProcessId,
                    CoreWebView2ProcessFailedKind.BrowserProcessExited,
                    WebViewProcessFailureAction.RecreateProcessGroup,
                    "callback-failed");
            }
        }

        return true;
    }

    public void RecordProcessFailure(
        string? containerProfileId,
        uint? browserProcessId,
        CoreWebView2ProcessFailedKind kind,
        WebViewProcessFailureAction action)
    {
        RecordRecoveryResult(containerProfileId, browserProcessId, kind, action, "observed");
    }

    public void RecordRecoveryResult(
        string? containerProfileId,
        uint? browserProcessId,
        CoreWebView2ProcessFailedKind kind,
        WebViewProcessFailureAction action,
        string result)
    {
        lock (_gate)
        {
            AddDiagnosticCore(new WebViewProcessRecoveryDiagnosticEntry(
                DateTimeOffset.UtcNow,
                GetDiagnosticScope(containerProfileId),
                browserProcessId,
                kind.ToString(),
                action.ToString(),
                result));
        }
    }

    public IReadOnlyList<WebViewProcessRecoveryDiagnosticEntry> GetDiagnosticHistory()
    {
        lock (_gate)
        {
            return _diagnostics.ToArray();
        }
    }

    private void AddDiagnosticCore(WebViewProcessRecoveryDiagnosticEntry entry)
    {
        while (_diagnostics.Count >= _capacity)
        {
            _diagnostics.Dequeue();
        }

        _diagnostics.Enqueue(entry);
    }

    private static string GetDiagnosticScope(string? containerProfileId)
    {
        return string.Equals(
            ContainerProfile.NormalizeId(containerProfileId),
            ContainerProfile.DefaultId,
            StringComparison.OrdinalIgnoreCase)
            ? "default"
            : "container";
    }
}
