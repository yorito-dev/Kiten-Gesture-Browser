using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace TabBrowser.Services;

public sealed class SingleInstanceService : IDisposable
{
    internal const string DefaultMutexName = @"Local\TabBrowser.SingleProcess.v1";
    internal const string DevelopmentMutexName = @"Local\TabBrowser.Development.SingleProcess.v1";

    private const int RestoreWindowCommand = 9;
    private readonly string _mutexName;
    private Mutex? _mutex;
    private bool _ownsMutex;

    public SingleInstanceService()
        : this(GetMutexName(ApplicationChannelService.Current))
    {
    }

    internal SingleInstanceService(string mutexName)
    {
        _mutexName = mutexName;
    }

    public bool TryAcquirePrimaryInstance()
    {
        if (_mutex is not null)
        {
            return _ownsMutex;
        }

        _mutex = new Mutex(initiallyOwned: false, _mutexName);
        try
        {
            _ownsMutex = _mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true;
        }

        return _ownsMutex;
    }

    public static bool TryActivateExistingInstance()
    {
        using var currentProcess = Process.GetCurrentProcess();
        var currentExecutablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExecutablePath))
        {
            return false;
        }

        var candidates = Process.GetProcessesByName(currentProcess.ProcessName)
            .Where(process => process.Id != currentProcess.Id && process.SessionId == currentProcess.SessionId)
            .OrderBy(process => process.StartTime)
            .ToArray();

        try
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                foreach (var process in candidates)
                {
                    process.Refresh();
                    var windowHandle = process.MainWindowHandle;
                    if (windowHandle == IntPtr.Zero
                        || !AreSameExecutablePath(
                            TryGetExecutablePath(process),
                            currentExecutablePath))
                    {
                        continue;
                    }

                    if (IsIconic(windowHandle))
                    {
                        ShowWindowAsync(windowHandle, RestoreWindowCommand);
                    }

                    return SetForegroundWindow(windowHandle);
                }

                Thread.Sleep(50);
            }

            return false;
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or System.ComponentModel.Win32Exception
                                          or NotSupportedException)
        {
            return false;
        }
        finally
        {
            foreach (var process in candidates)
            {
                process.Dispose();
            }
        }
    }

    internal static string GetMutexName(ApplicationChannel channel)
    {
        return channel == ApplicationChannel.Development
            ? DevelopmentMutexName
            : DefaultMutexName;
    }

    internal static bool AreSameExecutablePath(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
        {
            return false;
        }

        try
        {
            return Path.GetFullPath(first).Equals(
                Path.GetFullPath(second),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or NotSupportedException
                                          or PathTooLongException)
        {
            return false;
        }
    }

    private static string? TryGetExecutablePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or System.ComponentModel.Win32Exception
                                          or NotSupportedException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_ownsMutex)
        {
            try
            {
                _mutex?.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
        }

        _ownsMutex = false;
        _mutex?.Dispose();
        _mutex = null;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr windowHandle, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);
}
