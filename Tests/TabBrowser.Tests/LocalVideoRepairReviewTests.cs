using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using TabBrowser.Models;
using TabBrowser.Services;
using TabBrowser.Views;
using Xunit;

namespace TabBrowser.Tests;

public sealed class LocalVideoRepairReviewTests
{
    [Fact]
    public void ListRepairReusesLocalControllerAndRechecksRestoredFileWithoutYouTubeIdentity()
        => WorkspaceTrashWindowTests.RunInSta(async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "Kiten-local-repair-" + Guid.NewGuid().ToString("N") + ".avi");
            var state = new YouTubeMiniPlayerState { SourceKind = MiniPlayerSourceKind.LocalFile, LocalFilePath = path, Url = "" };
            // 公開用MediaElementと管理元VLCの両方で、実デコーダーを起動せず入口を検証する。
            var mediaType = typeof(LocalVideoPlaybackController).GetConstructors().Single().GetParameters()[0].ParameterType;
            var media = (FrameworkElement)Activator.CreateInstance(mediaType)!;
            var checkedFiles = new List<(string Path, bool Exists)>();
            Func<string, bool> checkPath = candidate =>
            {
                var exists = File.Exists(candidate);
                checkedFiles.Add((candidate, exists));
                return !exists;
            };
            using var controller = (LocalVideoPlaybackController)Activator.CreateInstance(
                typeof(LocalVideoPlaybackController), media, state, checkPath)!;
            var failures = new List<string>();
            controller.LoadFailed += failures.Add;
            // XAMLの操作はせず、既存viewのController窓口に実コントローラーを接続する。
            var view = (LocalVideoPlayerView)RuntimeHelpers.GetUninitializedObject(typeof(LocalVideoPlayerView));
            typeof(LocalVideoPlayerView).GetField("_controller", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, controller);
            var runtime = new YouTubeMiniPlayerRuntime(state, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
                null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null, view);
            var runtimes = new Dictionary<Guid, YouTubeMiniPlayerRuntime> { [state.Id] = runtime };
            Task<bool> Repair() => YouTubeMiniPlayerPlaybackCommandService.RepairAsync(state, runtimes,
                (_, _) => throw new Exception("YouTubeを読み込んではいけない"),
                _ => throw new Exception("YouTube復旧へ渡してはいけない"), _ => { });
            try
            {
                Assert.True(await Repair());
                media.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                Assert.Single(failures);
                Assert.Equal((path, false), Assert.Single(checkedFiles));
                Assert.Contains("見つかりません", failures[0]);
                File.WriteAllBytes(path, [0]);
                Assert.True(await Repair());
                media.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                Assert.Equal(new[] { (path, false), (path, true) }, checkedFiles);
                Assert.Equal(2, failures.Count);
                // デコーダーによる実再生成功を確認した試験ではない。
            }
            finally { controller.Dispose(); if (File.Exists(path)) File.Delete(path); }
        });
}
