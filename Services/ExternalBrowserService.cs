using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace TabBrowser.Services;

public enum ExternalBrowserKind
{
    MicrosoftEdge,
    GoogleChrome,
    WindowsDefault
}

public sealed record ExternalBrowserLaunchResult(
    bool Succeeded,
    int OpenedCount,
    string ErrorMessage);

public sealed class ExternalBrowserService
{
    private readonly Func<ExternalBrowserKind, string?> _resolveExecutablePath;
    private readonly Action<ProcessStartInfo> _startProcess;

    public ExternalBrowserService()
        : this(ResolveExecutablePath, static startInfo => _ = Process.Start(startInfo))
    {
    }

    public ExternalBrowserService(
        Func<ExternalBrowserKind, string?> resolveExecutablePath,
        Action<ProcessStartInfo> startProcess)
    {
        _resolveExecutablePath = resolveExecutablePath
            ?? throw new ArgumentNullException(nameof(resolveExecutablePath));
        _startProcess = startProcess
            ?? throw new ArgumentNullException(nameof(startProcess));
    }

    public bool IsAvailable(ExternalBrowserKind browserKind)
    {
        return browserKind == ExternalBrowserKind.WindowsDefault
            || !string.IsNullOrWhiteSpace(ResolveExecutablePathSafely(browserKind));
    }

    public ExternalBrowserLaunchResult Open(
        ExternalBrowserKind browserKind,
        IEnumerable<string> urls)
    {
        ArgumentNullException.ThrowIfNull(urls);
        var normalizedUrls = urls
            .Select(TryNormalizeWebUrl)
            .Where(static url => url is not null)
            .Select(static url => url!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalizedUrls.Count == 0)
        {
            return new ExternalBrowserLaunchResult(false, 0, "開けるWeb URLがありません");
        }

        var executablePath = browserKind == ExternalBrowserKind.WindowsDefault
            ? null
            : ResolveExecutablePathSafely(browserKind);
        if (browserKind != ExternalBrowserKind.WindowsDefault
            && string.IsNullOrWhiteSpace(executablePath))
        {
            return new ExternalBrowserLaunchResult(
                false,
                0,
                $"{GetDisplayName(browserKind)}が見つかりません");
        }

        var openedCount = 0;
        try
        {
            foreach (var url in normalizedUrls)
            {
                _startProcess(CreateStartInfo(browserKind, executablePath, url));
                openedCount++;
            }

            return new ExternalBrowserLaunchResult(true, openedCount, string.Empty);
        }
        catch (Exception exception)
        {
            return new ExternalBrowserLaunchResult(
                false,
                openedCount,
                $"{GetDisplayName(browserKind)}で開けません: {exception.Message}");
        }
    }

    public ExternalBrowserLaunchResult OpenApplicationWindow(
        ExternalBrowserKind browserKind,
        string url)
    {
        var normalizedUrl = TryNormalizeWebUrl(url);
        if (normalizedUrl is null)
        {
            return new ExternalBrowserLaunchResult(false, 0, "開けるWeb URLがありません");
        }

        if (browserKind == ExternalBrowserKind.WindowsDefault)
        {
            return Open(browserKind, [normalizedUrl]);
        }

        var executablePath = ResolveExecutablePathSafely(browserKind);
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return new ExternalBrowserLaunchResult(
                false,
                0,
                $"{GetDisplayName(browserKind)}が見つかりません");
        }

        try
        {
            var startInfo = new ProcessStartInfo(executablePath)
            {
                UseShellExecute = true
            };
            startInfo.ArgumentList.Add($"--app={normalizedUrl}");
            _startProcess(startInfo);
            return new ExternalBrowserLaunchResult(true, 1, string.Empty);
        }
        catch (Exception exception)
        {
            return new ExternalBrowserLaunchResult(
                false,
                0,
                $"{GetDisplayName(browserKind)}で開けません: {exception.Message}");
        }
    }

    public static bool IsSupportedWebUrl(string? value)
    {
        return TryNormalizeWebUrl(value) is not null;
    }

    public static string GetDisplayName(ExternalBrowserKind browserKind)
    {
        return browserKind switch
        {
            ExternalBrowserKind.MicrosoftEdge => "Microsoft Edge",
            ExternalBrowserKind.GoogleChrome => "Google Chrome",
            _ => "Windowsの既定ブラウザー"
        };
    }

    private static ProcessStartInfo CreateStartInfo(
        ExternalBrowserKind browserKind,
        string? executablePath,
        string url)
    {
        if (browserKind == ExternalBrowserKind.WindowsDefault)
        {
            return new ProcessStartInfo(url)
            {
                UseShellExecute = true
            };
        }

        var startInfo = new ProcessStartInfo(executablePath!)
        {
            UseShellExecute = true
        };
        startInfo.ArgumentList.Add(url);
        return startInfo;
    }

    private static string? TryNormalizeWebUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return uri.AbsoluteUri;
    }

    private string? ResolveExecutablePathSafely(ExternalBrowserKind browserKind)
    {
        try
        {
            return _resolveExecutablePath(browserKind);
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveExecutablePath(ExternalBrowserKind browserKind)
    {
        var executableName = browserKind switch
        {
            ExternalBrowserKind.MicrosoftEdge => "msedge.exe",
            ExternalBrowserKind.GoogleChrome => "chrome.exe",
            _ => string.Empty
        };
        if (string.IsNullOrEmpty(executableName))
        {
            return null;
        }

        foreach (var registryPath in EnumerateAppPathRegistryValues(executableName))
        {
            if (File.Exists(registryPath))
            {
                return registryPath;
            }
        }

        foreach (var candidate in EnumerateCommonInstallPaths(browserKind))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateAppPathRegistryValues(string executableName)
    {
        const string appPaths = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                RegistryKey? baseKey = null;
                RegistryKey? appKey = null;
                try
                {
                    baseKey = RegistryKey.OpenBaseKey(hive, view);
                    appKey = baseKey.OpenSubKey($@"{appPaths}\{executableName}");
                    if (appKey?.GetValue(null) is string value
                        && NormalizeExecutablePath(value) is { } path)
                    {
                        yield return path;
                    }
                }
                finally
                {
                    appKey?.Dispose();
                    baseKey?.Dispose();
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateCommonInstallPaths(
        ExternalBrowserKind browserKind)
    {
        var relativePath = browserKind == ExternalBrowserKind.MicrosoftEdge
            ? Path.Combine("Microsoft", "Edge", "Application", "msedge.exe")
            : Path.Combine("Google", "Chrome", "Application", "chrome.exe");
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                 })
        {
            if (!string.IsNullOrWhiteSpace(root))
            {
                yield return Path.Combine(root, relativePath);
            }
        }
    }

    private static string? NormalizeExecutablePath(string value)
    {
        var path = value.Trim().Trim('"');
        return Path.IsPathFullyQualified(path)
            ? path
            : null;
    }
}
