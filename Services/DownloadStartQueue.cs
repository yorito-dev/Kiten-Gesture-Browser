namespace TabBrowser.Services;

// WebView2のイベントから戻った後、同じWindowで保存先のモーダルを重ねずに扱う。
internal sealed class DownloadStartQueue
{
    private Task _tail = Task.CompletedTask;

    internal async Task EnqueueAsync(Func<Task> deferStart, Action run, Action complete)
    {
        var previous = _tail;
        var current = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _tail = current.Task;
        try
        {
            await deferStart();
            await previous;
            run();
        }
        finally
        {
            try
            {
                complete();
            }
            finally
            {
                current.TrySetResult();
            }
        }
    }
}
