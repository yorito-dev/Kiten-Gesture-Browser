using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using TabBrowser.Models;

namespace TabBrowser.Services;

public sealed class TabAudioSessionPanService
{
    private const int AudioSessionStateActive = 1;
    private const int ClsctxAll = 23;
    private readonly Dictionary<string, float[]> _baselineVolumes = [];

    public int ApplyPan(IEnumerable<int> processIds, TabAudioPan audioPan)
    {
        return ApplyPanWithDiagnostics(processIds, audioPan).AppliedSessionCount;
    }

    public TabAudioSessionPanApplyResult ApplyPanWithDiagnostics(
        IEnumerable<int> processIds,
        TabAudioPan audioPan,
        bool applyInactive = false)
    {
        var targetProcessIds = processIds
            .Where(static x => x >= 0)
            .ToHashSet();
        var diagnosticLines = new List<string>
        {
            $"targets={FormatProcessIds(targetProcessIds)}"
        };
        if (targetProcessIds.Count == 0)
        {
            diagnosticLines.Add("skip=empty-targets");
            return new TabAudioSessionPanApplyResult(0, 0, 0, diagnosticLines);
        }

        IMMDeviceEnumerator? deviceEnumerator = null;
        IMMDevice? device = null;
        IAudioSessionManager2? sessionManager = null;
        IAudioSessionEnumerator? sessionEnumerator = null;
        var appliedSessionCount = 0;
        var targetSessionCount = 0;
        var activeTargetSessionCount = 0;

        try
        {
            var deviceEnumeratorType = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
            if (deviceEnumeratorType is null)
            {
                diagnosticLines.Add("error=device-enumerator-type");
                return new TabAudioSessionPanApplyResult(0, 0, 0, diagnosticLines);
            }

            deviceEnumerator = (IMMDeviceEnumerator)Activator.CreateInstance(deviceEnumeratorType)!;
            if (deviceEnumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out device) < 0
                || device is null)
            {
                diagnosticLines.Add("error=default-endpoint");
                return new TabAudioSessionPanApplyResult(0, 0, 0, diagnosticLines);
            }

            var sessionManagerId = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref sessionManagerId, ClsctxAll, IntPtr.Zero, out var sessionManagerObject) < 0
                || sessionManagerObject is not IAudioSessionManager2 manager)
            {
                diagnosticLines.Add("error=session-manager");
                return new TabAudioSessionPanApplyResult(0, 0, 0, diagnosticLines);
            }

            sessionManager = manager;
            if (sessionManager.GetSessionEnumerator(out sessionEnumerator) < 0
                || sessionEnumerator is null
                || sessionEnumerator.GetCount(out var count) < 0)
            {
                diagnosticLines.Add("error=session-enumerator");
                return new TabAudioSessionPanApplyResult(0, 0, 0, diagnosticLines);
            }

            diagnosticLines.Add($"sessionCount={count.ToString(CultureInfo.InvariantCulture)}");
            for (var index = 0; index < count; index++)
            {
                var status = ApplyPanToSession(
                    sessionEnumerator,
                    index,
                    targetProcessIds,
                    audioPan,
                    applyInactive,
                    diagnosticLines);
                if (status.IsTarget)
                {
                    targetSessionCount++;
                }

                if (status.IsActiveTarget)
                {
                    activeTargetSessionCount++;
                }

                if (status.Applied)
                {
                    appliedSessionCount++;
                }
            }
        }
        catch (Exception ex)
        {
            // サイト側 JS で処理できない音声への補助経路なので、失敗しても通常ブラウジングは止めない。
            diagnosticLines.Add($"error={ex.GetType().Name}");
        }
        finally
        {
            ReleaseComObject(sessionEnumerator);
            ReleaseComObject(sessionManager);
            ReleaseComObject(device);
            ReleaseComObject(deviceEnumerator);
        }

        return new TabAudioSessionPanApplyResult(
            appliedSessionCount,
            targetSessionCount,
            activeTargetSessionCount,
            diagnosticLines);
    }

    private SessionApplyStatus ApplyPanToSession(
        IAudioSessionEnumerator sessionEnumerator,
        int index,
        IReadOnlySet<int> targetProcessIds,
        TabAudioPan audioPan,
        bool applyInactive,
        List<string> diagnosticLines)
    {
        IAudioSessionControl? sessionControl = null;
        var processId = 0u;
        var processName = string.Empty;
        var state = -1;
        var channelCount = 0u;
        var isTarget = false;
        var beforeVolumes = Array.Empty<float>();
        var baselineVolumes = Array.Empty<float>();
        var afterVolumes = Array.Empty<float>();
        var reason = string.Empty;

        try
        {
            if (sessionEnumerator.GetSession(index, out sessionControl) < 0
                || sessionControl is not IAudioSessionControl2 sessionControl2
                || sessionControl is not IChannelAudioVolume channelAudioVolume)
            {
                reason = "session-interface";
                return SessionApplyStatus.NotApplied;
            }

            if (sessionControl2.GetProcessId(out processId) < 0)
            {
                reason = "process-id";
                return SessionApplyStatus.NotApplied;
            }

            processName = GetProcessName(processId);
            isTarget = targetProcessIds.Contains((int)processId);
            _ = sessionControl.GetState(out state);

            if (channelAudioVolume.GetChannelCount(out channelCount) < 0
                || channelCount == 0
                || channelCount > 16)
            {
                reason = "channel-count";
                return new SessionApplyStatus(isTarget, isTarget && state == AudioSessionStateActive, false);
            }

            beforeVolumes = new float[channelCount];
            if (channelAudioVolume.GetAllVolumes(channelCount, beforeVolumes) < 0)
            {
                reason = "read-volumes";
                return new SessionApplyStatus(isTarget, isTarget && state == AudioSessionStateActive, false);
            }

            if (!isTarget)
            {
                reason = "not-target";
                return SessionApplyStatus.NotApplied;
            }

            if (audioPan != TabAudioPan.Stereo && !applyInactive && state != AudioSessionStateActive)
            {
                reason = "inactive";
                return new SessionApplyStatus(true, false, false);
            }

            if (audioPan != TabAudioPan.Stereo && channelCount < 2)
            {
                reason = "mono";
                return new SessionApplyStatus(true, true, false);
            }

            var sessionKey = GetSessionKey(sessionControl2, processId, index);
            if (!_baselineVolumes.TryGetValue(sessionKey, out baselineVolumes)
                || baselineVolumes.Length != beforeVolumes.Length)
            {
                baselineVolumes = CreateBaselineVolumes(beforeVolumes);
                _baselineVolumes[sessionKey] = baselineVolumes;
            }
            else
            {
                baselineVolumes = CreateBaselineVolumes(baselineVolumes);
                _baselineVolumes[sessionKey] = baselineVolumes;
            }

            var nextVolumes = CreatePannedVolumes(baselineVolumes, audioPan);
            var eventContext = Guid.Empty;
            if (channelAudioVolume.SetAllVolumes((uint)nextVolumes.Length, nextVolumes, ref eventContext) < 0)
            {
                reason = "write-volumes";
                return new SessionApplyStatus(true, state == AudioSessionStateActive, false);
            }

            afterVolumes = new float[channelCount];
            _ = channelAudioVolume.GetAllVolumes(channelCount, afterVolumes);
            return new SessionApplyStatus(true, state == AudioSessionStateActive, true);
        }
        catch (Exception ex)
        {
            reason = ex.GetType().Name;
            return new SessionApplyStatus(isTarget, isTarget && state == AudioSessionStateActive, false);
        }
        finally
        {
            diagnosticLines.Add(string.Join(
                ",",
                $"index={index.ToString(CultureInfo.InvariantCulture)}",
                $"pid={processId.ToString(CultureInfo.InvariantCulture)}",
                $"name={NormalizeDiagnosticValue(processName)}",
                $"state={state.ToString(CultureInfo.InvariantCulture)}",
                $"channels={channelCount.ToString(CultureInfo.InvariantCulture)}",
                $"target={isTarget}",
                $"before={FormatVolumes(beforeVolumes)}",
                $"baseline={FormatVolumes(baselineVolumes ?? Array.Empty<float>())}",
                $"after={FormatVolumes(afterVolumes)}",
                $"reason={NormalizeDiagnosticValue(reason)}"));
            ReleaseComObject(sessionControl);
        }
    }

    private static string GetSessionKey(IAudioSessionControl2 sessionControl, uint processId, int index)
    {
        try
        {
            if (sessionControl.GetSessionInstanceIdentifier(out var instanceIdentifier) >= 0
                && !string.IsNullOrWhiteSpace(instanceIdentifier))
            {
                return instanceIdentifier;
            }
        }
        catch
        {
        }

        return $"{processId}:{index}";
    }

    private static float[] CreateBaselineVolumes(IReadOnlyList<float> currentVolumes)
    {
        var baselineVolumes = currentVolumes.ToArray();
        if (baselineVolumes.Length < 2)
        {
            return baselineVolumes;
        }

        var maxVolume = baselineVolumes.Max();
        if (maxVolume <= 0)
        {
            return baselineVolumes;
        }

        var silentThreshold = maxVolume * 0.05f;
        if (baselineVolumes.Any(volume => volume <= silentThreshold)
            && baselineVolumes.Any(volume => volume > silentThreshold))
        {
            for (var index = 0; index < baselineVolumes.Length; index++)
            {
                baselineVolumes[index] = maxVolume;
            }
        }

        return baselineVolumes;
    }

    private static float[] CreatePannedVolumes(IReadOnlyList<float> baselineVolumes, TabAudioPan audioPan)
    {
        var nextVolumes = baselineVolumes.ToArray();
        if (nextVolumes.Length < 2)
        {
            return nextVolumes;
        }

        switch (audioPan)
        {
            case TabAudioPan.Left:
                for (var index = 0; index < nextVolumes.Length; index++)
                {
                    nextVolumes[index] = index == 0 ? baselineVolumes[index] : 0;
                }

                break;
            case TabAudioPan.Right:
                for (var index = 0; index < nextVolumes.Length; index++)
                {
                    nextVolumes[index] = index == 1 ? baselineVolumes[index] : 0;
                }

                break;
            default:
                for (var index = 0; index < nextVolumes.Length; index++)
                {
                    nextVolumes[index] = baselineVolumes[index];
                }

                break;
        }

        return nextVolumes;
    }

    private static string GetProcessName(uint processId)
    {
        if (processId == 0 || processId > int.MaxValue)
        {
            return string.Empty;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string FormatProcessIds(IEnumerable<int> processIds)
    {
        return string.Join(",", processIds.OrderBy(static x => x));
    }

    private static string FormatVolumes(IReadOnlyList<float> volumes)
    {
        return volumes.Count == 0
            ? "-"
            : string.Join(",", volumes.Select(static volume => volume.ToString("0.###", CultureInfo.InvariantCulture)));
    }

    private static string NormalizeDiagnosticValue(string value)
    {
        return string.Join(
            " ",
            (value ?? string.Empty).Split(
                ['\r', '\n', '\t', ',', '|'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static void ReleaseComObject(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            _ = Marshal.ReleaseComObject(comObject);
        }
    }

    private readonly record struct SessionApplyStatus(bool IsTarget, bool IsActiveTarget, bool Applied)
    {
        public static SessionApplyStatus NotApplied { get; } = new(false, false, false);
    }

    private enum EDataFlow
    {
        Render = 0,
        Capture = 1,
        All = 2
    }

    private enum ERole
    {
        Console = 0,
        Multimedia = 1,
        Communications = 2
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, IntPtr devices);

        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(
            ref Guid interfaceId,
            int classContext,
            IntPtr activationParameters,
            [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport]
    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        int GetAudioSessionControl(IntPtr audioSessionGuid, uint streamFlags, out IAudioSessionControl sessionControl);

        int GetSimpleAudioVolume(IntPtr audioSessionGuid, uint streamFlags, IntPtr audioVolume);

        int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnumerator);

        int RegisterSessionNotification(IntPtr sessionNotification);

        int UnregisterSessionNotification(IntPtr sessionNotification);

        int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr duckNotification);

        int UnregisterDuckNotification(IntPtr duckNotification);
    }

    [ComImport]
    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        int GetCount(out int sessionCount);

        int GetSession(int sessionIndex, out IAudioSessionControl sessionControl);
    }

    [ComImport]
    [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        int GetState(out int state);

        int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);

        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName, ref Guid eventContext);

        int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);

        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, ref Guid eventContext);

        int GetGroupingParam(out Guid groupingId);

        int SetGroupingParam(ref Guid groupingId, ref Guid eventContext);

        int RegisterAudioSessionNotification(IntPtr newNotifications);

        int UnregisterAudioSessionNotification(IntPtr newNotifications);
    }

    [ComImport]
    [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2 : IAudioSessionControl
    {
        int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string retVal);

        int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string retVal);

        int GetProcessId(out uint retVal);

        int IsSystemSoundsSession();

        int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport]
    [Guid("1C158861-B533-4B30-B1CF-E853E51C59B8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IChannelAudioVolume
    {
        int GetChannelCount(out uint channelCount);

        int SetChannelVolume(uint channelIndex, float volumeLevel, ref Guid eventContext);

        int GetChannelVolume(uint channelIndex, out float volumeLevel);

        int SetAllVolumes(
            uint channelCount,
            [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] float[] volumeLevels,
            ref Guid eventContext);

        int GetAllVolumes(
            uint channelCount,
            [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] float[] volumeLevels);
    }
}

public sealed class TabAudioSessionPanApplyResult
{
    public TabAudioSessionPanApplyResult(
        int appliedSessionCount,
        int targetSessionCount,
        int activeTargetSessionCount,
        IReadOnlyList<string> diagnosticLines)
    {
        AppliedSessionCount = appliedSessionCount;
        TargetSessionCount = targetSessionCount;
        ActiveTargetSessionCount = activeTargetSessionCount;
        DiagnosticLines = diagnosticLines;
    }

    public int AppliedSessionCount { get; }

    public int TargetSessionCount { get; }

    public int ActiveTargetSessionCount { get; }

    public IReadOnlyList<string> DiagnosticLines { get; }
}
