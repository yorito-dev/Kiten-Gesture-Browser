using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using TabBrowser.Models;

namespace TabBrowser.Services;

public sealed class ApplicationCatalogService
{
    public const int MaximumCatalogItems = 2048;

    private static readonly HashSet<string> LaunchableFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe",
        ".com",
        ".cpl",
        ".msc",
        ".lnk",
        ".appref-ms"
    };
    private static readonly object CacheGate = new();
    private static readonly SemaphoreSlim CatalogLoadGate = new(1, 1);
    private static IReadOnlyList<ApplicationLaunchItem>? s_cachedApplications;

    public async Task<IReadOnlyList<ApplicationLaunchItem>> GetApplicationsAsync(
        CancellationToken cancellationToken = default)
    {
        lock (CacheGate)
        {
            if (s_cachedApplications is { } cached)
            {
                return cached;
            }
        }

        await CatalogLoadGate.WaitAsync(cancellationToken);
        try
        {
            lock (CacheGate)
            {
                if (s_cachedApplications is { } cached)
                {
                    return cached;
                }
            }

            var applications = await LoadWindowsApplicationsAsync(cancellationToken);
            lock (CacheGate)
            {
                s_cachedApplications = applications;
            }

            return applications;
        }
        finally
        {
            CatalogLoadGate.Release();
        }
    }

    public static void InvalidateCache()
    {
        lock (CacheGate)
        {
            s_cachedApplications = null;
        }
    }

    public static ApplicationLaunchItem? CreateDiscoveredItem(string? displayName, string? launchTarget)
    {
        var normalizedName = NormalizeDisplayName(displayName);
        var normalizedTarget = NormalizeLaunchTarget(launchTarget);
        if (normalizedName.Length == 0
            || normalizedTarget.Length == 0
            || IsUninstallEntry(normalizedName, normalizedTarget))
        {
            return null;
        }

        ApplicationLaunchKind kind;
        string sourceLabel;
        if (Path.IsPathFullyQualified(normalizedTarget))
        {
            try
            {
                normalizedTarget = Path.GetFullPath(normalizedTarget);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }

            if (!LaunchableFileExtensions.Contains(Path.GetExtension(normalizedTarget)))
            {
                return null;
            }

            kind = ApplicationLaunchKind.ShellPath;
            sourceLabel = "デスクトップアプリ";
        }
        else
        {
            kind = ApplicationLaunchKind.AppsFolder;
            sourceLabel = normalizedTarget.Contains('!', StringComparison.Ordinal)
                ? "Microsoft Store アプリ"
                : "Windowsアプリ";
        }

        return new ApplicationLaunchItem
        {
            Id = CreateStableId(kind, normalizedTarget),
            DisplayName = normalizedName,
            LaunchTarget = normalizedTarget,
            Kind = kind,
            SourceLabel = sourceLabel
        };
    }

    public static IReadOnlyList<ApplicationLaunchItem> NormalizeCatalog(
        IEnumerable<ApplicationLaunchItem>? applications)
    {
        var normalized = new Dictionary<string, ApplicationLaunchItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var application in applications ?? [])
        {
            if (application is null
                || string.IsNullOrWhiteSpace(application.DisplayName)
                || string.IsNullOrWhiteSpace(application.LaunchTarget))
            {
                continue;
            }

            var key = $"{application.Kind}:{application.LaunchTarget.Trim()}";
            normalized.TryAdd(key, application);
        }

        return normalized.Values
            .OrderBy(static application => application.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(static application => application.LaunchTarget, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumCatalogItems)
            .ToList();
    }

    public static ProcessStartInfo CreateLaunchStartInfo(ApplicationLaunchItem application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (string.IsNullOrWhiteSpace(application.LaunchTarget))
        {
            throw new ArgumentException("アプリの起動先が空です。", nameof(application));
        }

        var fileName = application.Kind switch
        {
            ApplicationLaunchKind.ShellPath when Path.IsPathFullyQualified(application.LaunchTarget) =>
                Path.GetFullPath(application.LaunchTarget),
            ApplicationLaunchKind.AppsFolder when IsSafeAppsFolderTarget(application.LaunchTarget) =>
                "shell:AppsFolder\\" + application.LaunchTarget,
            _ => throw new ArgumentException("安全に起動できないアプリです。", nameof(application))
        };
        return new ProcessStartInfo(fileName)
        {
            UseShellExecute = true
        };
    }

    public void Launch(ApplicationLaunchItem application)
    {
        if (application.Kind == ApplicationLaunchKind.ShellPath
            && !File.Exists(application.LaunchTarget))
        {
            throw new FileNotFoundException("アプリが見つかりません。", application.LaunchTarget);
        }

        if (Process.Start(CreateLaunchStartInfo(application)) is null)
        {
            throw new InvalidOperationException("アプリを起動できませんでした。");
        }
    }

    private static Task<IReadOnlyList<ApplicationLaunchItem>> LoadWindowsApplicationsAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<IReadOnlyList<ApplicationLaunchItem>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<ApplicationLaunchItem> applications;
                try
                {
                    applications = EnumerateAppsFolder(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    applications = [];
                }

                if (applications.Count == 0)
                {
                    applications = EnumerateStartMenu(cancellationToken);
                }

                completion.TrySetResult(NormalizeCatalog(applications));
            }
            catch (OperationCanceledException)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "TabBrowser Application Catalog"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static IReadOnlyList<ApplicationLaunchItem> EnumerateAppsFolder(
        CancellationToken cancellationToken)
    {
        var applications = new List<ApplicationLaunchItem>();
        object? shellObject = null;
        object? folderObject = null;
        object? itemsObject = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application", throwOnError: false)
                ?? throw new InvalidOperationException("Windows Shellを利用できません。");
            shellObject = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("Windows Shellを初期化できません。");
            dynamic shell = shellObject;
            folderObject = shell.NameSpace("shell:AppsFolder");
            if (folderObject is null)
            {
                return applications;
            }

            dynamic folder = folderObject;
            itemsObject = folder.Items();
            if (itemsObject is null)
            {
                return applications;
            }

            dynamic items = itemsObject;
            var count = Math.Max(0, Convert.ToInt32(items.Count, CultureInfo.InvariantCulture));
            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                object? itemObject = null;
                try
                {
                    itemObject = items.Item(index);
                    if (itemObject is null)
                    {
                        continue;
                    }

                    dynamic item = itemObject;
                    if (Convert.ToBoolean(item.IsFolder, CultureInfo.InvariantCulture))
                    {
                        continue;
                    }

                    var application = CreateDiscoveredItem(
                        Convert.ToString(item.Name, CultureInfo.CurrentCulture),
                        Convert.ToString(item.Path, CultureInfo.InvariantCulture));
                    if (application is not null)
                    {
                        applications.Add(application);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // 壊れたShell項目だけを除外し、残りのアプリ列挙を継続する。
                }
                finally
                {
                    ReleaseComObject(itemObject);
                }
            }
        }
        finally
        {
            ReleaseComObject(itemsObject);
            ReleaseComObject(folderObject);
            ReleaseComObject(shellObject);
        }

        return applications;
    }

    private static IReadOnlyList<ApplicationLaunchItem> EnumerateStartMenu(
        CancellationToken cancellationToken)
    {
        var applications = new List<ApplicationLaunchItem>();
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)
        };
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            try
            {
                foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!LaunchableFileExtensions.Contains(Path.GetExtension(path)))
                    {
                        continue;
                    }

                    var application = CreateDiscoveredItem(
                        Path.GetFileNameWithoutExtension(path),
                        path);
                    if (application is not null)
                    {
                        applications.Add(application);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 読めないスタートメニューフォルダーは無視する。
            }
        }

        return applications;
    }

    private static string CreateStableId(ApplicationLaunchKind kind, string target)
    {
        var source = $"{kind}\n{target.Trim().ToUpperInvariant()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(source));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string NormalizeDisplayName(string? value)
    {
        return string.Join(
                " ",
                (value ?? string.Empty)
                    .Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Trim();
    }

    private static string NormalizeLaunchTarget(string? value)
    {
        var target = (value ?? string.Empty).Trim();
        return IsSafeAppsFolderTarget(target) ? target : string.Empty;
    }

    private static bool IsSafeAppsFolderTarget(string target)
    {
        return !string.IsNullOrWhiteSpace(target)
            && !target.Any(char.IsControl)
            && !target.Contains('"', StringComparison.Ordinal)
            && !target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUninstallEntry(string displayName, string target)
    {
        if (displayName.StartsWith("Uninstall ", StringComparison.OrdinalIgnoreCase)
            || displayName.Contains("アンインストール", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var fileName = Path.GetFileNameWithoutExtension(target);
        return fileName.Equals("uninstall", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("unins", StringComparison.OrdinalIgnoreCase);
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is null || !Marshal.IsComObject(value))
        {
            return;
        }

        try
        {
            _ = Marshal.FinalReleaseComObject(value);
        }
        catch
        {
        }
    }
}
