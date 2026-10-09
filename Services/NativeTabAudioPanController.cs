using Microsoft.Web.WebView2.Core;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal interface INativeTabAudioPanProcessSource
{
    uint BrowserProcessId { get; }
    int CurrentProcessId { get; }
    Task<IEnumerable<int>> GetMatchingProcessIdsAsync(string? currentHost, bool isRadiko);
    IEnumerable<int> GetEnvironmentProcessIds();
}

// PID収集と補助適用の順序を所有する。実音声セッションは既存service、診断出力は呼出元へ任せる。
internal sealed class NativeTabAudioPanController
{
    private readonly Func<IEnumerable<int>, TabAudioPan, bool, Task<TabAudioSessionPanApplyResult>> _applyAsync;

    internal NativeTabAudioPanController(
        Func<IEnumerable<int>, TabAudioPan, bool, Task<TabAudioSessionPanApplyResult>>? applyAsync = null)
    {
        if (applyAsync is not null)
        {
            _applyAsync = applyAsync;
            return;
        }

        var sessionService = new TabAudioSessionPanService();
        _applyAsync = (processIds, audioPan, applyInactive) => Task.Run(
            () => sessionService.ApplyPanWithDiagnostics(processIds, audioPan, applyInactive));
    }

    public async Task ApplyAsync(
        INativeTabAudioPanProcessSource source,
        Func<string?> getCurrentHost,
        TabAudioPan audioPan,
        Action<string, IReadOnlyCollection<int>, TabAudioSessionPanApplyResult> appendDiagnostic)
    {
        try
        {
            var isRadiko = IsRadiko(getCurrentHost());
            var processIds = await GetProcessIdsAsync(source, getCurrentHost);
            var result = await _applyAsync(processIds, audioPan, false);
            if (isRadiko)
            {
                appendDiagnostic("primary", processIds, result);
            }

            if (result.AppliedSessionCount == 0 && isRadiko)
            {
                var fallbackProcessIds = processIds.ToHashSet();
                AddEnvironmentProcessIds(source, fallbackProcessIds);
                AddCurrentProcessId(source, fallbackProcessIds);
                if (fallbackProcessIds.Count > processIds.Count)
                {
                    var fallbackResult = await _applyAsync(fallbackProcessIds, audioPan, false);
                    appendDiagnostic("fallback", fallbackProcessIds, fallbackResult);
                }

                var pidZeroResult = await _applyAsync([0], audioPan, true);
                appendDiagnostic("pid0", [0], pidZeroResult);
            }
        }
        catch
        {
            // Windows 音声セッション fallback は補助経路なので、失敗してもページ操作は止めない。
        }
    }

    private static async Task<IReadOnlyCollection<int>> GetProcessIdsAsync(
        INativeTabAudioPanProcessSource source, Func<string?> getCurrentHost)
    {
        var processIds = new HashSet<int>();
        if (source.BrowserProcessId <= int.MaxValue)
        {
            processIds.Add((int)source.BrowserProcessId);
        }

        var currentHost = getCurrentHost();
        var isRadiko = IsRadiko(getCurrentHost());
        if (isRadiko)
        {
            AddCurrentProcessId(source, processIds);
        }

        try
        {
            var matchingProcessIds = await source.GetMatchingProcessIdsAsync(currentHost, isRadiko);
            foreach (var processId in matchingProcessIds)
            {
                processIds.Add(processId);
            }

            if (isRadiko && processIds.Count <= 1)
            {
                AddEnvironmentProcessIds(source, processIds);
            }
        }
        catch
        {
            if (isRadiko)
            {
                AddEnvironmentProcessIds(source, processIds);
            }
        }

        return processIds;
    }

    private static bool IsRadiko(string? host)
        => !string.IsNullOrWhiteSpace(host) && BrowserUrlRulesService.HostMatches(host, "radiko.jp");

    private static void AddCurrentProcessId(INativeTabAudioPanProcessSource source, ISet<int> processIds)
    {
        if (source.CurrentProcessId > 0)
        {
            processIds.Add(source.CurrentProcessId);
        }
    }

    private static void AddEnvironmentProcessIds(INativeTabAudioPanProcessSource source, ISet<int> processIds)
    {
        try
        {
            foreach (var processId in source.GetEnvironmentProcessIds())
            {
                if (processId > 0)
                {
                    processIds.Add(processId);
                }
            }
        }
        catch
        {
        }
    }
}

// Coreの情報は遅延列挙し、列挙途中の失敗でもそれまでに集めたPIDを維持する。
internal sealed class WebViewAudioPanProcessSource(CoreWebView2 core) : INativeTabAudioPanProcessSource
{
    public uint BrowserProcessId => core.BrowserProcessId;
    public int CurrentProcessId => Environment.ProcessId;

    public async Task<IEnumerable<int>> GetMatchingProcessIdsAsync(string? currentHost, bool isRadiko)
    {
        var extendedInfos = await core.Environment.GetProcessExtendedInfosAsync();
        return EnumerateMatchingProcessIds(extendedInfos, currentHost, isRadiko);
    }

    private static IEnumerable<int> EnumerateMatchingProcessIds(
        IEnumerable<CoreWebView2ProcessExtendedInfo> extendedInfos, string? currentHost, bool isRadiko)
    {
        foreach (var info in extendedInfos)
        {
            var processId = info.ProcessInfo.ProcessId;
            if (processId <= 0)
            {
                continue;
            }

            if (ProcessInfoMatchesHost(info, currentHost)
                || (isRadiko && (info.ProcessInfo.Kind == CoreWebView2ProcessKind.Utility
                    || ProcessInfoMatchesHost(info, "radiko.jp"))))
            {
                yield return processId;
            }
        }
    }

    public IEnumerable<int> GetEnvironmentProcessIds()
    {
        foreach (var processInfo in core.Environment.GetProcessInfos())
        {
            if (processInfo.ProcessId > 0)
            {
                yield return processInfo.ProcessId;
            }
        }
    }

    private static bool ProcessInfoMatchesHost(CoreWebView2ProcessExtendedInfo processInfo, string? targetHost)
    {
        if (string.IsNullOrWhiteSpace(targetHost))
        {
            return false;
        }

        foreach (var frameInfo in processInfo.AssociatedFrameInfos)
        {
            if (FrameInfoMatchesHost(frameInfo, targetHost))
            {
                return true;
            }
        }

        return false;
    }

    private static bool FrameInfoMatchesHost(CoreWebView2FrameInfo frameInfo, string targetHost)
    {
        for (var current = frameInfo; current is not null; current = current.ParentFrameInfo)
        {
            var host = Uri.TryCreate(current.Source ?? string.Empty, UriKind.Absolute, out var uri)
                ? uri.Host
                : null;
            if (!string.IsNullOrWhiteSpace(host)
                && (BrowserUrlRulesService.HostMatches(host, targetHost)
                    || BrowserUrlRulesService.HostMatches(targetHost, host)))
            {
                return true;
            }
        }

        return false;
    }
}
