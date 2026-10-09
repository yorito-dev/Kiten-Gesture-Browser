using System.IO;
using System.Windows;
using TabBrowser.Models;
using TabBrowser.Services.Workspaces;

namespace TabBrowser.Services;

internal interface ILocalVideoMiniPlayerHost
{
    int WindowIndex { get; }
    double HostWidth { get; }
    double HostHeight { get; }
    IReadOnlyList<YouTubeMiniPlayerState> States { get; }
    YouTubeMiniPlayerRuntime CreateRuntime(YouTubeMiniPlayerState state);
    void Register(YouTubeMiniPlayerState state, YouTubeMiniPlayerRuntime runtime);
    void SetPosition(YouTubeMiniPlayerState state, YouTubeMiniPlayerRuntime runtime);
    void BringToFront(Guid id);
    void LoadMedia(YouTubeMiniPlayerRuntime runtime);
    YouTubeMiniPlayerRuntime? FindRuntime(Guid id);
    void ClampToHost(YouTubeMiniPlayerState state, YouTubeMiniPlayerRuntime runtime);
    void UpdateTitle(YouTubeMiniPlayerState state);
    string? SelectFile(string? initialPath);
    void StartExplorer(string filePath);
    void UpdateCaptureTimer();
    void QueueSave();
    Task RefreshListAsync();
    void RecordRecentFile(string filePath);
    void SetStatus(string message);
}

// ファイルを開く・差し替える手順と初回サイズ調整を所有し、実再生とWPF操作は既存経路へ接続する。
internal sealed class LocalVideoMiniPlayerController(
    ILocalVideoMiniPlayerHost host,
    Func<string, bool>? fileExists = null,
    Func<DateTimeOffset>? utcNow = null)
{
    private readonly Func<string, bool> _fileExists = fileExists ?? File.Exists;
    private readonly Func<DateTimeOffset> _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);

    public void ApplyAspectRatio(YouTubeMiniPlayerState state, double videoWidth, double videoHeight)
    {
        if (state.SourceKind != MiniPlayerSourceKind.LocalFile
            || !double.IsFinite(videoWidth) || !double.IsFinite(videoHeight)
            || videoWidth <= 0 || videoHeight <= 0)
        {
            return;
        }

        var aspectRatio = Math.Clamp(videoWidth / videoHeight, 0.2, 5);
        var shouldResize = state.VideoAspectRatio is null;
        if (state.VideoAspectRatio is { } currentRatio && Math.Abs(currentRatio - aspectRatio) < 0.001)
        {
            return;
        }

        state.VideoAspectRatio = aspectRatio;
        state.UpdatedAt = _utcNow();
        if (shouldResize && host.FindRuntime(state.Id) is { } runtime)
        {
            var size = YouTubeMiniPlayerLayoutService.NormalizeSizeToVideo(
                state.Width, state.Height, preferHeight: false, aspectRatio: aspectRatio);
            state.Width = size.Width;
            state.Height = size.Height;
            host.ClampToHost(state, runtime);
        }

        host.QueueSave();
    }

    public void Relink(YouTubeMiniPlayerState state, Func<string, bool> replaceFile)
    {
        var selectedPath = host.SelectFile(state.LocalFilePath);
        if (!string.IsNullOrWhiteSpace(selectedPath))
        {
            ReplaceFile(state, replaceFile, selectedPath);
        }
    }

    public void ReplaceFile(YouTubeMiniPlayerState state, Func<string, bool> replaceFile, string filePath)
    {
        if (!replaceFile(filePath))
        {
            host.SetStatus("対応している動画ファイルを選択してください");
            return;
        }

        host.UpdateTitle(state);
        host.QueueSave();
        _ = host.RefreshListAsync();
    }

    public void ShowInExplorer(YouTubeMiniPlayerState state)
    {
        if (string.IsNullOrWhiteSpace(state.LocalFilePath))
        {
            host.SetStatus("動画ファイルの場所を開けません");
            return;
        }

        try
        {
            host.StartExplorer(state.LocalFilePath);
        }
        catch (Exception ex)
        {
            host.SetStatus("動画ファイルの場所を開けませんでした: " + ex.Message);
        }
    }

    public bool Open(string filePath)
    {
        if (!TryNormalizePath(filePath, out var normalizedPath))
        {
            host.SetStatus("対応している動画ファイルを選択してください");
            return false;
        }

        var bounds = YouTubeMiniPlayerLayoutService.GetNextAvailableBounds(
            isShorts: false,
            host.States.Select(static state => new Rect(state.Left, state.Top, state.Width, state.Height)).ToList(),
            host.HostWidth, host.HostHeight);
        var state = YouTubeMiniPlayerStateFactory.CreateLocalFile(host.WindowIndex, normalizedPath, bounds, _utcNow());
        var runtime = host.CreateRuntime(state);
        host.Register(state, runtime);
        host.SetPosition(state, runtime);
        host.BringToFront(state.Id);
        host.LoadMedia(runtime);
        host.UpdateCaptureTimer();
        host.QueueSave();
        _ = host.RefreshListAsync();
        host.RecordRecentFile(normalizedPath);
        host.SetStatus("ローカル動画をミニプレイヤーで開きました");
        return true;
    }

    public bool TryNormalizePath(string? filePath, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        try
        {
            var candidate = Path.GetFullPath(filePath);
            if (!_fileExists(candidate) || !WorkspaceShellItemService.IsVideoPath(candidate))
            {
                return false;
            }

            normalizedPath = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public IReadOnlyList<string> NormalizeDroppedPaths(IEnumerable<string> paths)
        => paths.Select(path => TryNormalizePath(path, out var normalizedPath) ? normalizedPath : null)
            .Where(static path => path is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
