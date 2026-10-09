using System.Runtime.ExceptionServices;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal sealed class TabRuntimeLifetimeService(TabRuntimeRegistry registry)
{
    public void ReleaseResources(BrowserTab tab, Action<TabRuntime> releaseResources)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(releaseResources);
        var runtime = registry.GetOrCreate(tab);
        releaseResources(runtime);
    }

    public void Terminate(BrowserTab tab, Action<TabRuntime> releaseResources)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(releaseResources);
        var runtime = registry.GetOrCreate(tab);
        Exception? releaseException = null;
        try
        {
            releaseResources(runtime);
        }
        catch (Exception ex)
        {
            releaseException = ex;
        }

        Exception? terminationException = null;
        try
        {
            _ = runtime.Terminate();
        }
        catch (Exception ex)
        {
            terminationException = ex;
        }

        if (releaseException is not null && terminationException is not null)
        {
            throw new AggregateException(
                "タブの終端処理をすべて完了できませんでした。",
                releaseException,
                terminationException);
        }

        if (releaseException is not null)
        {
            ExceptionDispatchInfo.Capture(releaseException).Throw();
        }

        if (terminationException is not null)
        {
            ExceptionDispatchInfo.Capture(terminationException).Throw();
        }
    }
}
