using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using TabBrowser.Services;
using TabBrowser.Views;
using Xunit;

namespace TabBrowser.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WorkspaceNoteEditWindowStaCollection
{
    public const string Name = "Workspace note editor STA";
}

[Collection(WorkspaceNoteEditWindowStaCollection.Name)]
public sealed class WorkspaceNoteEditWindowTests
{
    [Fact]
    public void SuppressedDuplicateEditorIsClosedWithoutChangingExistingNote()
    {
        RunInSta(() =>
        {
            var owner = CreateHiddenOwner();
            var existing = new WorkspaceNoteEditWindow("メモを編集", "失わない編集文章")
            {
                Owner = owner,
                Opacity = 0,
                ShowActivated = false,
                ShowInTaskbar = false
            };
            var duplicate = new WorkspaceNoteEditWindow("メモを追加", string.Empty) { Owner = owner };
            var duplicateClosed = false;
            duplicate.Closed += (_, _) => duplicateClosed = true;
            try
            {
                var existingCompletion = ModelessWindowService.ShowAsync(existing);
                var duplicateCompletion = ModelessWindowService.ShowAsync(duplicate);

                Assert.False(duplicateCompletion.GetAwaiter().GetResult());
                Assert.True(duplicateClosed);
                Assert.False(duplicate.IsLoaded);
                Assert.True(existing.IsLoaded);
                Assert.False(existingCompletion.IsCompleted);
                Assert.Equal("失わない編集文章", existing.EditorContent);

                existing.Close();
                Assert.True(existingCompletion.GetAwaiter().GetResult());
                Assert.Empty(owner.OwnedWindows.OfType<WorkspaceNoteEditWindow>());
            }
            finally
            {
                duplicate.Close();
                existing.Close();
                owner.Close();
            }
        });
    }

    [Fact]
    public void NeverShownEditorDoesNotRequireCloseConfirmation()
    {
        RunInSta(() =>
        {
            var window = new WorkspaceNoteEditWindow("メモを追加", string.Empty);
            try
            {
                Assert.False(window.IsLoaded);
                Assert.False(window.IsVisible);
                Assert.False(window.RequiresCloseConfirmation);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void HiddenEditorIsProtectedAndShownWithoutLosingContent()
    {
        RunInSta(() =>
        {
            var owner = CreateHiddenOwner();
            var window = new WorkspaceNoteEditWindow("メモを編集", "非表示でも失わない編集文章")
            {
                Owner = owner,
                Opacity = 0,
                ShowActivated = false,
                ShowInTaskbar = false
            };
            try
            {
                window.Show();
                window.Hide();

                Assert.False(window.IsVisible);
                Assert.True(window.RequiresCloseConfirmation);
                window.ShowRequiredCloseAction();

                Assert.True(window.IsVisible);
                Assert.True(window.RequiresCloseConfirmation);
                Assert.Equal("非表示でも失わない編集文章", window.EditorContent);
                Assert.Contains("保存して閉じるかキャンセル", window.StatusText);
            }
            finally
            {
                window.Close();
                owner.Close();
            }
        });
    }

    [Fact]
    public void PendingSaveIsProtectedEvenBeforeEditorIsShown()
    {
        RunInSta(() =>
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var window = new WorkspaceNoteEditWindow("メモを編集", "保存中の本文")
            {
                SaveAndCloseAsync = (_, _) => completion.Task
            };
            try
            {
                var save = window.TrySaveAndCloseAsync();
                Assert.False(window.IsVisible);
                Assert.True(window.IsSaving);
                Assert.True(window.RequiresCloseConfirmation);

                completion.SetResult(true);
                PumpUntil(() => save.IsCompleted);
                Assert.True(save.Result);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void CloseAllBlockShowsEditorWithoutChangingItsContent()
    {
        RunInSta(() =>
        {
            var owner = CreateHiddenOwner();
            var window = new WorkspaceNoteEditWindow("メモを編集", "編集中の本文") { Owner = owner };
            window.Show();
            window.WindowState = WindowState.Minimized;

            window.ShowRequiredCloseAction();

            Assert.Equal(WindowState.Normal, window.WindowState);
            Assert.Contains("保存して閉じるかキャンセル", window.StatusText);
            Assert.Equal("編集中の本文", window.EditorContent);
            Assert.True(window.IsCloseConfirmationVisible);
            var confirmation = Assert.IsType<Window>(window.CloseConfirmationWindow);
            Assert.Same(window, confirmation.Owner);
            Assert.Equal(WindowStartupLocation.CenterScreen, confirmation.WindowStartupLocation);
            Assert.True(confirmation.Topmost);
            Assert.NotSame(window.Content, confirmation.Content);
            window.DismissCloseConfirmation();
            Assert.False(window.IsCloseConfirmationVisible);
            Assert.Null(window.CloseConfirmationWindow);
            Assert.True(window.IsVisible);
            Assert.Equal("編集中の本文", window.EditorContent);
            window.ShowRequiredCloseAction();
            Assert.True(window.IsCloseConfirmationVisible);
            Assert.NotSame(confirmation, window.CloseConfirmationWindow);
            var reopened = window.CloseConfirmationWindow;
            window.ShowRequiredCloseAction();
            Assert.Same(reopened, window.CloseConfirmationWindow);
            reopened!.Close();
            Assert.True(window.IsVisible);
            Assert.Equal("編集中の本文", window.EditorContent);
            window.Close();
            owner.Close();
        });
    }

    [Fact]
    public void DiscardAllRequestDoesNotSaveAndFailureRetainsEditor()
    {
        RunInSta(() =>
        {
            var window = new WorkspaceNoteEditWindow("メモを編集", "未保存の本文");
            var pending = new TaskCompletionSource<WindowPersistenceResult>();
            var calls = 0;
            window.CloseAllWithoutSavingAsync = () => { calls++; return pending.Task; };
            window.SaveAndCloseAsync = (_, _) => throw new Exception("メモは保存しない");
            try
            {
                window.Show();
                window.ShowRequiredCloseAction();
                var request = window.RequestCloseAllWithoutSavingAsync();
                window.RequestCloseAllWithoutSavingAsync().GetAwaiter().GetResult();
                Assert.Equal(1, calls);
                Assert.False(window.TrySaveAndCloseAsync().GetAwaiter().GetResult());
                Assert.True(window.IsEditorReadOnly);
                pending.SetResult(new([new("設定", WindowPersistenceStatus.Failed)]));
                PumpUntil(() => request.IsCompleted);
                request.GetAwaiter().GetResult();
                Assert.True(window.IsVisible);
                Assert.True(window.IsCloseConfirmationVisible);
                Assert.False(window.IsEditorReadOnly);
                Assert.False(window.IsAccepted);
                Assert.Equal("未保存の本文", window.EditorContent);
                Assert.Contains("終了を中止", window.StatusText);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void SaveAndCloseWaitsForPendingSaveBeforeClosing()
    {
        RunInSta(() =>
        {
            var saveCompletion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var saveCallCount = 0;
            var owner = CreateHiddenOwner();
            var window = new WorkspaceNoteEditWindow("メモを編集", "編集中の本文");
            window.UpdateWithoutClosingAsync = (_, _) =>
            {
                saveCallCount++;
                return saveCompletion.Task;
            };
            window.Owner = owner;
            window.Show();

            InvokeTryAccept(window);
            var wasOpenWhileSaving = window.IsLoaded;
            saveCompletion.TrySetResult(true);
            PumpUntil(() => !window.IsLoaded);

            Assert.Equal(1, saveCallCount);
            Assert.True(wasOpenWhileSaving);
            Assert.False(window.IsLoaded);
            owner.Close();
        });
    }

    [Fact]
    public void SaveFailureKeepsContentAndFontSizeThenRetryClosesOnce()
    {
        RunInSta(() =>
        {
            var saveCallCount = 0;
            var owner = CreateHiddenOwner();
            var window = new WorkspaceNoteEditWindow(
                "メモを編集",
                "失敗後も残す本文",
                initialFontSize: 22);
            window.SaveAndCloseAsync = (_, _) =>
            {
                saveCallCount++;
                return Task.FromResult(saveCallCount > 1);
            };
            window.Owner = owner;
            window.Show();

            var failed = window.TrySaveAndCloseAsync().GetAwaiter().GetResult();

            Assert.False(failed);
            Assert.True(window.IsLoaded);
            Assert.Equal("失敗後も残す本文", window.EditorContent);
            Assert.Equal(22, window.NoteFontSize);
            Assert.Contains("再試行", window.StatusText);
            Assert.False(window.IsEditorReadOnly);

            var succeeded = window.TrySaveAndCloseAsync().GetAwaiter().GetResult();
            PumpUntil(() => !window.IsLoaded);

            Assert.True(succeeded);
            Assert.Equal(2, saveCallCount);
            Assert.True(window.IsAccepted);
            Assert.Equal("失敗後も残す本文", window.NoteContent);
            Assert.False(window.IsLoaded);
            owner.Close();
        });
    }

    [Fact]
    public void PendingSaveRejectsDuplicateAndCloseWhileEditorIsReadOnly()
    {
        RunInSta(() =>
        {
            var saveCompletion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var saveCallCount = 0;
            var owner = CreateHiddenOwner();
            var window = new WorkspaceNoteEditWindow("メモを編集", "保留中の本文");
            window.SaveAndCloseAsync = (_, _) =>
            {
                saveCallCount++;
                return saveCompletion.Task;
            };
            window.Owner = owner;
            window.Show();

            var pending = window.TrySaveAndCloseAsync();
            var duplicate = window.TrySaveAndCloseAsync();
            window.Close();

            Assert.True(window.IsLoaded);
            Assert.True(window.IsSaving);
            Assert.True(window.IsEditorReadOnly);
            Assert.True(duplicate.IsCompletedSuccessfully);
            Assert.False(duplicate.Result);
            Assert.Equal(1, saveCallCount);
            Assert.Contains("保存中", window.StatusText);

            saveCompletion.TrySetResult(true);
            PumpUntil(() => pending.IsCompleted && !window.IsLoaded);

            Assert.True(pending.Result);
            Assert.Equal(1, saveCallCount);
            Assert.True(window.IsAccepted);
            owner.Close();
        });
    }

    [Fact]
    public void UpdateInProgressRejectsSaveAndCloseUntilTheNextExplicitSave()
    {
        RunInSta(() =>
        {
            var updateCompletion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var saveCallCount = 0;
            var owner = CreateHiddenOwner();
            var window = new WorkspaceNoteEditWindow("メモを編集", "更新する本文");
            window.UpdateWithoutClosingAsync = (_, _) =>
            {
                saveCallCount++;
                return updateCompletion.Task;
            };
            window.Owner = owner;
            window.Show();

            var pendingUpdate = window.TrySaveWithoutClosingAsync();
            var rejectedClose = window.TrySaveAndCloseAsync();

            Assert.True(rejectedClose.IsCompletedSuccessfully);
            Assert.False(rejectedClose.Result);
            Assert.Equal(1, saveCallCount);
            Assert.True(window.IsLoaded);

            updateCompletion.TrySetResult(true);
            PumpUntil(() => pendingUpdate.IsCompleted);

            Assert.True(pendingUpdate.Result);
            Assert.True(window.IsLoaded);
            Assert.False(window.IsAccepted);

            var savedAndClosed = window.TrySaveAndCloseAsync().GetAwaiter().GetResult();
            PumpUntil(() => !window.IsLoaded);

            Assert.True(savedAndClosed);
            Assert.Equal(2, saveCallCount);
            Assert.False(window.IsLoaded);
            owner.Close();
        });
    }

    private static void InvokeTryAccept(WorkspaceNoteEditWindow window)
    {
        var method = typeof(WorkspaceNoteEditWindow).GetMethod(
            "TryAccept",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(window, null);
    }

    private static Window CreateHiddenOwner()
    {
        var owner = new Window
        {
            Width = 1,
            Height = 1,
            Left = -10000,
            Top = -10000,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None
        };
        owner.Show();
        return owner;
    }

    private static void PumpUntil(Func<bool> condition)
    {
        if (condition())
        {
            return;
        }

        var frame = new DispatcherFrame();
        var timedOut = false;
        var deadline = DateTime.UtcNow.AddSeconds(2);
        var timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(10),
            DispatcherPriority.Background,
            (_, _) =>
            {
                if (condition() || DateTime.UtcNow >= deadline)
                {
                    timedOut = !condition();
                    frame.Continue = false;
                }
            },
            Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        Assert.False(timedOut, "Windowの状態遷移が期限内に完了しませんでした。");
    }

    private static void RunInSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "STAテストが期限内に完了しませんでした。");
        if (failure is not null)
        {
            throw failure;
        }
    }
}
