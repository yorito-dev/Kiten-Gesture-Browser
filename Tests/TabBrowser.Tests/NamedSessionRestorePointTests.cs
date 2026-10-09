using System.Text.Json;
using System.Text.Json.Nodes;
using TabBrowser.Models;
using TabBrowser.Services;
using TabBrowser.Views;
using Xunit;

namespace TabBrowser.Tests;

public sealed class NamedSessionRestorePointTests
{
    [Theory]
    [InlineData(8, 0, 0, "8 タブを新しいWindowへ追加しました。現在のタブとWorkspaceはそのままです")]
    [InlineData(8, 1, 1, "8 タブを追加しました。1 タブはコンテナ不明で保留、1 タブは復元失敗です。元の復元点は変更していません")]
    [InlineData(0, 2, 0, "タブは追加されませんでした。2 タブはコンテナ不明で保留です。元の復元点は変更していません")]
    [InlineData(0, 0, 2, "タブは追加されませんでした。2 タブは復元失敗です。元の復元点は変更していません")]
    public void RestoreStatusDistinguishesActualSuccessDeferredAndFailure(
        int restored,
        int deferred,
        int failed,
        string expected)
        => Assert.Equal(expected, SessionSnapshotService.CreateNamedRestoreStatus(restored, deferred, failed));

    [Fact]
    public async Task ManualPointSurvivesBothAutomaticRotationsAndServiceRecreation()
    {
        using var files = new RestorePointFiles();
        var service = files.Service();
        var request = Request();
        var saved = await service.SaveNamedRestorePointAsync(request);
        var original = await File.ReadAllBytesAsync(files.Point(request.Id));
        for (var index = 0; index < 16; index++)
        {
            await service.SaveAsync(new SessionData { Tabs = [new() { Url = $"https://example.test/{index}" }] });
            await service.SaveAsync(new SessionData());
        }
        Assert.Equal(12, Directory.GetFiles(Path.Combine(files.DirectoryPath, "session.protected"), "*.json").Length);
        Assert.Equal(6, Directory.GetFiles(Path.Combine(files.DirectoryPath, "session.snapshots"), "*.json").Length);
        Assert.Equal(original, await File.ReadAllBytesAsync(files.Point(request.Id)));
        var reader = files.Service();
        var info = Assert.Single(await reader.GetNamedRestorePointsAsync());
        Assert.Equal(saved, info);
        Assert.Equal(request.Name, info.Name);
        Assert.Equal(request.CreatedAt, info.CreatedAt);
        Assert.Equal(2, info.TabCount);
        Assert.Equal(1, info.WindowCount);
        Assert.Contains("Workspace・メモ・認証情報は対象外", info.ScopeLabel);
        var loaded = await reader.LoadNamedRestorePointAsync(info.Id);
        Assert.Equal(request.Session.Windows[0].Tabs[1].Id, loaded.Windows[0].Tabs[1].Id);
        Assert.Empty(loaded.ClosedTabs);
        Assert.Empty(loaded.ClosedWindows);
        Assert.Empty(loaded.ClosedYouTubeMiniPlayers);
        Assert.Null(loaded.WindowMemoryYouTubeMiniPlayers);
        Assert.Empty(loaded.Windows[0].ClosedTabs);
        Assert.Empty((await reader.LoadAsync()).Tabs);
    }

    [Fact]
    public async Task RetryIsIdempotentAndCollisionCancellationAndFailedWriteKeepTheGoodPoint()
    {
        using var files = new RestorePointFiles();
        var request = Request();
        var saves = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => files.Service().SaveNamedRestorePointAsync(request)));
        Assert.All(saves, info => Assert.Equal(saves[0], info));
        Assert.Single(await files.Service().GetNamedRestorePointsAsync());
        var bytes = await File.ReadAllBytesAsync(files.Point(request.Id));
        await Assert.ThrowsAsync<IOException>(() => files.Service().SaveNamedRestorePointAsync(request with { Name = "衝突" }));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => files.Service().SaveNamedRestorePointAsync(Request(), canceled.Token));
        var failed = Request();
        Directory.CreateDirectory(files.Point(failed.Id));
        await Assert.ThrowsAnyAsync<IOException>(() => files.Service().SaveNamedRestorePointAsync(failed));
        Directory.Delete(files.Point(failed.Id));
        await files.Service().SaveNamedRestorePointAsync(failed);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(files.Point(request.Id)));
        Assert.Equal(2, (await files.Service().GetNamedRestorePointsAsync()).Count);
        Assert.Empty(Directory.GetFiles(files.DirectoryPath, "*.tmp", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("broken")]
    [InlineData("version")]
    [InlineData("metadata")]
    [InlineData("windows")]
    [InlineData("tab")]
    [InlineData("empty")]
    public async Task BrokenOrUnsupportedPointDoesNotFallBackOrDamageOtherPoints(string fault)
    {
        using var files = new RestorePointFiles();
        var service = files.Service();
        var good = await service.SaveNamedRestorePointAsync(Request());
        var bad = await service.SaveNamedRestorePointAsync(Request());
        var json = JsonNode.Parse(await File.ReadAllTextAsync(files.Point(bad.Id)))!;
        switch (fault)
        {
            case "version": json["version"] = 999; break;
            case "metadata": json["namedRestorePoint"]!["version"] = 999; break;
            case "windows": json["windows"] = new JsonArray((JsonNode?)null); break;
            case "tab": json["windows"]![0]!["tabs"] = new JsonArray((JsonNode?)null); break;
            case "empty": json["tabs"] = new JsonArray(); json["windows"] = new JsonArray(); break;
        }
        var damaged = fault == "broken" ? "{途中" : json.ToJsonString();
        await File.WriteAllTextAsync(files.Point(bad.Id), damaged);
        await File.WriteAllTextAsync(files.Point(Guid.NewGuid()) + ".tmp", "途中保存");
        var items = await files.Service().GetNamedRestorePointsAsync();
        Assert.Equal(2, items.Count);
        Assert.True(items.Single(x => x.Id == good.Id).CanRestore);
        Assert.False(items.Single(x => x.Id == bad.Id).CanRestore);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.LoadNamedRestorePointAsync(bad.Id));
        Assert.Equal(damaged, await File.ReadAllTextAsync(files.Point(bad.Id)));
        Assert.False(File.Exists(Path.Combine(files.DirectoryPath, "session.json")));
        Assert.Equal(good.ContentHash, (await service.GetNamedRestorePointsAsync()).Single(x => x.Id == good.Id).ContentHash);
    }

    [Fact]
    public async Task RestorePlanPreservesContainerIdsAndUsesNewIdsWithoutMutatingSavedState()
    {
        using var files = new RestorePointFiles();
        var request = Request();
        var window = request.Session.Windows[0];
        var customId = Guid.NewGuid().ToString("N");
        window.Tabs[1].ContainerProfileId = customId;
        window.Tabs.Add(new SessionTabState { Url = "https://example.test/missing", ContainerProfileId = Guid.NewGuid().ToString("N") });
        window.Tabs.Add(new SessionTabState { Url = "https://example.test/invalid", ContainerProfileId = "invalid" });
        window.Tabs.Add(new SessionTabState { Url = "https://example.test/legacy", ContainerProfileId = null! });
        var service = files.Service();
        var saved = await service.SaveNamedRestorePointAsync(request);
        var loaded = await files.Service().LoadNamedRestorePointAsync(saved.Id);
        var before = JsonSerializer.Serialize(loaded);
        var plan = SessionSnapshotService.CreateNamedRestorePlan(loaded);
        var restored = Assert.Single(plan.Windows);
        Assert.Equal(5, restored.Tabs.Count);
        Assert.NotEqual(window.WindowId, restored.WindowId);
        Assert.Equal(restored.Tabs[1].Id, restored.LastActiveTabId);
        Assert.Equal(customId, restored.Tabs[1].ContainerProfileId);
        Assert.Equal(window.Tabs[2].ContainerProfileId, restored.Tabs[2].ContainerProfileId);
        Assert.Equal("invalid", restored.Tabs[3].ContainerProfileId);
        Assert.Null(restored.Tabs[4].ContainerProfileId);
        Assert.DoesNotContain(restored.Tabs[0].Id, window.Tabs.Select(x => x.Id));
        Assert.NotEqual(window.Tabs[0].SplitViewId, restored.Tabs[0].SplitViewId);
        Assert.Equal(restored.Tabs[0].SplitViewId, restored.Tabs[1].SplitViewId);
        Assert.Equal(0.65, restored.Tabs[0].SplitViewRatio);
        Assert.True(restored.Tabs[0].IsPinned);
        Assert.True(restored.Tabs[0].IsMuted);
        Assert.Equal(6, restored.Tabs[0].AudioBoostDecibels);
        Assert.Equal(12.5, restored.Tabs[0].YouTubePlaybackPositionSeconds);
        Assert.Equal(window.Tabs[0].NavigationHistory, restored.Tabs[0].NavigationHistory);
        Assert.Equal(window.Tabs[1].FolderTabState!.SelectedPaths, restored.Tabs[1].FolderTabState!.SelectedPaths);
        Assert.Equal(before, JsonSerializer.Serialize(loaded));
        Assert.Equal(saved.ContentHash, Assert.Single(await service.GetNamedRestorePointsAsync()).ContentHash);
        var second = SessionSnapshotService.CreateNamedRestorePlan(loaded);
        Assert.NotNull(second.Windows[0].Tabs[0].SplitViewId);
        Assert.NotEqual(restored.Tabs[0].Id, second.Windows[0].Tabs[0].Id);
        var missingOnly = new SessionData { Tabs = [new() { Url = "https://example.test/", ContainerProfileId = "missing" }] };
        Assert.Single(SessionSnapshotService.CreateNamedRestorePlan(missingOnly).Windows);
    }

    [Fact]
    public async Task ConfirmationHashAndConcurrentDeletionProtectChangedOrAlreadyDeletedPoints()
    {
        using var files = new RestorePointFiles();
        var service = files.Service();
        var saved = await service.SaveNamedRestorePointAsync(Request());
        await File.AppendAllTextAsync(files.Point(saved.Id), " ");
        await Assert.ThrowsAsync<IOException>(() => service.DeleteNamedRestorePointAsync(saved.Id, saved.ContentHash));
        await Assert.ThrowsAsync<IOException>(() => service.LoadNamedRestorePointAsync(saved.Id, expectedContentHash: saved.ContentHash));
        var current = Assert.Single(await service.GetNamedRestorePointsAsync());
        var errors = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Record.ExceptionAsync(
            () => files.Service().DeleteNamedRestorePointAsync(current.Id, current.ContentHash))));
        Assert.Single(errors, error => error is null);
        Assert.Equal(3, errors.Count(error => error is FileNotFoundException));
        Assert.Empty(await service.GetNamedRestorePointsAsync());
    }

    [Fact]
    public async Task AtomicNewFileFailureAndCollisionNeverReplaceAnExistingFile()
    {
        using var files = new RestorePointFiles();
        var path = Path.Combine(files.DirectoryPath, "good.json");
        await File.WriteAllTextAsync(path, "正常");
        await Assert.ThrowsAsync<IOException>(() => AtomicFileService.WriteAsync(path, null,
            stream => stream.WriteAsync("置換"u8.ToArray()).AsTask(), overwrite: false));
        await Assert.ThrowsAsync<IOException>(() => AtomicFileService.WriteAsync(path, null, async stream =>
        {
            await stream.WriteAsync("途中"u8.ToArray());
            throw new IOException("検証用の書込失敗");
        }, overwrite: false));
        Assert.Equal("正常", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(files.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public void WindowRetryKeepsSnapshotAndDeletionRequiresConfirmation() => WorkspaceTrashWindowTests.RunInSta(async () =>
    {
        using var files = new RestorePointFiles();
        var service = files.Service();
        var captured = 0;
        var restored = 0;
        var captureGate = new TaskCompletionSource<SessionData>();
        var window = new NamedSessionRestorePointsWindow(service, () => { captured++; return captureGate.Task; }, session =>
        {
            restored += session.Windows[0].Tabs.Count;
            return Task.FromResult("追加Windowへ復元しました");
        });
        try
        {
            window.SetName("長く残す");
            // 検証フォルダーだけを塞ぎ、実データに触れず容量不足等と同じ保存失敗境界を作る。
            await File.WriteAllTextAsync(service.NamedRestorePointDirectory, "検証用障害");
            var first = window.SaveAsync();
            await window.SaveAsync();
            Assert.Equal(1, captured);
            captureGate.SetResult(Request().Session);
            await first;
            Assert.DoesNotContain("保存しました", window.StatusText);
            File.Delete(service.NamedRestorePointDirectory);
            await window.SaveAsync();
            Assert.Equal(1, captured);
            Assert.Contains("保存しました", window.StatusText);
            var point = Assert.Single(await service.GetNamedRestorePointsAsync());
            Assert.Equal("長く残す", point.Name);
            await window.RestoreAsync();
            Assert.Equal(2, restored);
            await window.ConfirmDeleteAsync();
            Assert.Single(await service.GetNamedRestorePointsAsync());
            window.PrepareDelete();
            await window.ConfirmDeleteAsync();
            Assert.Contains("削除しました", window.StatusText);
            Assert.Empty(await service.GetNamedRestorePointsAsync());
        }
        finally { window.Close(); }
    });

    [Fact]
    public void CommittedSaveIsNotReportedAsFailedWhenOnlyTheListRefreshFails() => WorkspaceTrashWindowTests.RunInSta(async () =>
    {
        using var files = new RestorePointFiles();
        var failRefresh = true;
        var window = new NamedSessionRestorePointsWindow(files.Service(), () => Task.FromResult(Request().Session), _ => Task.FromResult("復元"))
        {
            BeforeReloadAsync = () => failRefresh ? Task.FromException(new IOException("一覧取得失敗")) : Task.CompletedTask
        };
        try
        {
            window.SetName("保存済み");
            await window.SaveAsync();
            Assert.Contains("保存しました", window.StatusText);
            Assert.Contains("再保存せず", window.StatusText);
            Assert.Single(await files.Service().GetNamedRestorePointsAsync());
            failRefresh = false;
            await window.RefreshAsync();
            Assert.Single(await files.Service().GetNamedRestorePointsAsync());
        }
        finally { window.Close(); }
    });

    private static NamedSessionRestorePointRequest Request()
    {
        var split = Guid.NewGuid().ToString("N");
        var first = new SessionTabState
        {
            Url = "https://example.test/one",
            Title = "タブ1",
            IsPinned = true,
            IsMuted = true,
            AudioBoostDecibels = 6,
            NavigationHistory = ["https://example.test/", "https://example.test/one"],
            NavigationHistoryIndex = 1,
            SplitViewId = split,
            SplitViewOrientation = SplitViewOrientation.TopBottom,
            SplitViewRatio = 0.65,
            YouTubePlaybackPositionSeconds = 12.5
        };
        var second = new SessionTabState
        {
            Url = "C:\\test",
            Title = "フォルダー",
            IsFolderTab = true,
            SplitViewId = split,
            SplitViewOrientation = SplitViewOrientation.TopBottom,
            SplitViewRatio = 0.65,
            FolderTabState = new() { SelectedPaths = ["C:\\test\\item"], DetailScrollOffset = 25 }
        };
        var session = new SessionData
        {
            Version = 2,
            Windows = [new() { Tabs = [first, second], LastActiveTabId = second.Id, ClosedTabs = [new() { Url = "https://example.test/closed" }] }],
            ClosedTabs = [new() { Url = "https://example.test/closed" }],
            ClosedWindows = [new() { WindowState = new() { Tabs = [new() { Url = "https://example.test/closed-window" }] } }]
        };
        return new NamedSessionRestorePointRequest(Guid.NewGuid(), "調査の区切り", DateTimeOffset.UtcNow, session);
    }

    private sealed class RestorePointFiles : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "TabBrowserRestorePointTests", Guid.NewGuid().ToString("N"));
        public RestorePointFiles() => Directory.CreateDirectory(DirectoryPath);
        public SessionService Service() => new(Path.Combine(DirectoryPath, "session.json"), Path.Combine(DirectoryPath, "window-memory.json"), DirectoryPath);
        public string Point(Guid id) => Path.Combine(DirectoryPath, "session.named", $"named-{id:N}.json");
        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }
}
