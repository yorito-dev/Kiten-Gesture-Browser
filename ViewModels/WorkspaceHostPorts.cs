using System.Windows.Media;
using TabBrowser.Models.Workspaces;
using TabBrowser.Services.Workspaces;

namespace TabBrowser.ViewModels;

// Workspaceからホストへの呼出しだけを束ねる。状態・資源・寿命の所有権は移さない。
public sealed record WorkspaceTabPort(
    Func<string, Task> OpenUrlAsync,
    Func<IReadOnlyList<SessionTab>, Task<int>> OpenSessionTabsAsync,
    Func<IReadOnlyList<CapturedTabSnapshot>> CaptureOpenTabs,
    Func<CapturedTabSnapshot?> CaptureCurrentTab,
    Func<Guid, Task> CloseTabAsync,
    Action<string> OpenShellFolderInTab,
    Func<string?>? GetDeferredRestoreMessage = null);

public sealed record WorkspaceMiniPlayerPort(
    Func<IReadOnlyList<YouTubeMiniPlayerWorkspaceState>> Capture,
    Func<Task<IReadOnlyList<YouTubeMiniPlayerWorkspaceState>>> CaptureCurrentAsync,
    Func<YouTubeMiniPlayerSetItem, Task> OpenSetAsync);

public sealed record WorkspaceWindowPort(
    Func<IReadOnlyList<SessionTab>, Task> OpenSessionTabsAsync,
    Action<FolderItem, Guid?> OpenFolder,
    Action<Guid> SaveLastWorkspaceId,
    Func<string?>? GetDeferredRestoreMessage = null);

public sealed record WorkspaceThumbnailPort(
    Func<string, string, Task<ImageSource?>> CaptureUrlAsync);
