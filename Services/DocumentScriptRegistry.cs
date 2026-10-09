using System.Runtime.CompilerServices;
using Microsoft.Web.WebView2.Core;

namespace TabBrowser.Services;

internal sealed class DocumentScriptRegistry
{
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private readonly object _sync = new();
    private readonly Dictionary<string, ScriptRegistration> _fixedScripts = [];
    private readonly Dictionary<string, ScriptRegistration> _variableScripts = [];
    private readonly Dictionary<string, long> _latestVariableRequests = [];
    private long _nextVariableRequest;
    private bool _isCleared;

    internal int FixedScriptCount
    {
        get
        {
            lock (_sync)
            {
                return _fixedScripts.Count;
            }
        }
    }

    internal int VariableScriptCount
    {
        get
        {
            lock (_sync)
            {
                return _variableScripts.Count;
            }
        }
    }

    public async Task EnsureFixedAsync(
        string key,
        string script,
        Func<string, Task<string>> addScriptAsync,
        Action<string> removeScript)
    {
        ValidateArguments(key, script, addScriptAsync, removeScript);
        await _updateGate.WaitAsync();
        try
        {
            lock (_sync)
            {
                ThrowIfCleared();
                if (_fixedScripts.TryGetValue(key, out var current))
                {
                    if (!string.Equals(current.Script, script, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"固定DocumentCreatedスクリプト '{key}' の内容が変更されました。");
                    }

                    return;
                }
            }

            var scriptId = await addScriptAsync(script);
            lock (_sync)
            {
                if (_isCleared)
                {
                    TryRemove(removeScript, scriptId);
                    throw new ObjectDisposedException(nameof(DocumentScriptRegistry));
                }

                _fixedScripts.Add(key, new ScriptRegistration(script, scriptId));
            }
        }
        finally
        {
            _updateGate.Release();
        }
    }

    public async Task UpdateVariableAsync(
        string key,
        string script,
        Func<string, Task<string>> addScriptAsync,
        Action<string> removeScript)
    {
        ValidateArguments(key, script, addScriptAsync, removeScript);
        long request;
        lock (_sync)
        {
            ThrowIfCleared();
            request = ++_nextVariableRequest;
            _latestVariableRequests[key] = request;
        }

        await _updateGate.WaitAsync();
        try
        {
            lock (_sync)
            {
                ThrowIfCleared();
                if (_latestVariableRequests[key] != request)
                {
                    return;
                }

                if (_variableScripts.TryGetValue(key, out var current)
                    && string.Equals(current.Script, script, StringComparison.Ordinal))
                {
                    return;
                }
            }

            var scriptId = await addScriptAsync(script);
            lock (_sync)
            {
                if (_isCleared)
                {
                    TryRemove(removeScript, scriptId);
                    throw new ObjectDisposedException(nameof(DocumentScriptRegistry));
                }

                if (_latestVariableRequests[key] != request)
                {
                    TryRemove(removeScript, scriptId);
                    return;
                }

                if (_variableScripts.TryGetValue(key, out var previous))
                {
                    try
                    {
                        removeScript(previous.ScriptId);
                    }
                    catch
                    {
                        TryRemove(removeScript, scriptId);
                        throw;
                    }
                }

                _variableScripts[key] = new ScriptRegistration(script, scriptId);
            }
        }
        finally
        {
            _updateGate.Release();
        }
    }

    public bool ContainsVariable(string key)
    {
        lock (_sync)
        {
            return !_isCleared && _variableScripts.ContainsKey(key);
        }
    }

    public void RemoveVariable(string key, Action<string> removeScript)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(removeScript);

        lock (_sync)
        {
            if (_isCleared)
            {
                return;
            }

            _latestVariableRequests[key] = ++_nextVariableRequest;
            if (!_variableScripts.TryGetValue(key, out var current))
            {
                return;
            }

            removeScript(current.ScriptId);
            _variableScripts.Remove(key);
        }
    }

    public void Clear(Action<string> removeScript)
    {
        ArgumentNullException.ThrowIfNull(removeScript);
        List<string> scriptIds;
        lock (_sync)
        {
            if (_isCleared)
            {
                return;
            }

            _isCleared = true;
            scriptIds = _fixedScripts.Values
                .Concat(_variableScripts.Values)
                .Select(static registration => registration.ScriptId)
                .ToList();
            _fixedScripts.Clear();
            _variableScripts.Clear();
            _latestVariableRequests.Clear();
        }

        foreach (var scriptId in scriptIds)
        {
            TryRemove(removeScript, scriptId);
        }
    }

    private static void ValidateArguments(
        string key,
        string script,
        Func<string, Task<string>> addScriptAsync,
        Action<string> removeScript)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(addScriptAsync);
        ArgumentNullException.ThrowIfNull(removeScript);
    }

    private void ThrowIfCleared()
    {
        ObjectDisposedException.ThrowIf(_isCleared, this);
    }

    private static void TryRemove(Action<string> removeScript, string scriptId)
    {
        try
        {
            removeScript(scriptId);
        }
        catch
        {
            // WebView2終了と競合した場合も管理状態は確実に破棄する。
        }
    }

    private sealed record ScriptRegistration(string Script, string ScriptId);
}

internal sealed class DocumentScriptRegistryStore<TWebView>
    where TWebView : class
{
    private readonly ConditionalWeakTable<TWebView, DocumentScriptRegistry> _registries = new();

    public Task EnsureFixedAsync(
        TWebView webView,
        string key,
        string script,
        Func<TWebView, string, Task<string>> addScriptAsync,
        Action<TWebView, string> removeScript)
    {
        var registry = GetRegistry(webView);
        return registry.EnsureFixedAsync(
            key,
            script,
            value => addScriptAsync(webView, value),
            id => removeScript(webView, id));
    }

    public Task UpdateVariableAsync(
        TWebView webView,
        string key,
        string script,
        Func<TWebView, string, Task<string>> addScriptAsync,
        Action<TWebView, string> removeScript)
    {
        var registry = GetRegistry(webView);
        return registry.UpdateVariableAsync(
            key,
            script,
            value => addScriptAsync(webView, value),
            id => removeScript(webView, id));
    }

    public bool ContainsVariable(TWebView webView, string key) =>
        _registries.TryGetValue(webView, out var registry)
        && registry.ContainsVariable(key);

    public void RemoveVariable(
        TWebView webView,
        string key,
        Action<TWebView, string> removeScript)
    {
        if (_registries.TryGetValue(webView, out var registry))
        {
            registry.RemoveVariable(key, id => removeScript(webView, id));
        }
    }

    public void Clear(TWebView webView, Action<TWebView, string> removeScript)
    {
        if (!_registries.TryGetValue(webView, out var registry))
        {
            return;
        }

        _registries.Remove(webView);
        registry.Clear(id => removeScript(webView, id));
    }

    internal DocumentScriptRegistry GetRegistry(TWebView webView) =>
        _registries.GetValue(webView, static _ => new DocumentScriptRegistry());
}

internal static class CoreWebViewDocumentScriptRegistry
{
    private static readonly DocumentScriptRegistryStore<CoreWebView2> Registries = new();

    public static Task EnsureFixedAsync(CoreWebView2 core, string key, string script) =>
        Registries.EnsureFixedAsync(
            core,
            key,
            script,
            static (target, value) => target.AddScriptToExecuteOnDocumentCreatedAsync(value),
            static (target, id) => target.RemoveScriptToExecuteOnDocumentCreated(id));

    public static Task UpdateVariableAsync(CoreWebView2 core, string key, string script) =>
        Registries.UpdateVariableAsync(
            core,
            key,
            script,
            static (target, value) => target.AddScriptToExecuteOnDocumentCreatedAsync(value),
            static (target, id) => target.RemoveScriptToExecuteOnDocumentCreated(id));

    public static bool ContainsVariable(CoreWebView2 core, string key) =>
        Registries.ContainsVariable(core, key);

    public static void RemoveVariable(CoreWebView2 core, string key) =>
        Registries.RemoveVariable(
            core,
            key,
            static (target, id) => target.RemoveScriptToExecuteOnDocumentCreated(id));

    public static void Clear(CoreWebView2 core) =>
        Registries.Clear(
            core,
            static (target, id) => target.RemoveScriptToExecuteOnDocumentCreated(id));
}
