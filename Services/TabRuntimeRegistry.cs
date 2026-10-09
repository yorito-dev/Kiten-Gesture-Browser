using System.Runtime.CompilerServices;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal sealed class TabRuntimeRegistry
{
    private readonly ConditionalWeakTable<BrowserTab, TabRuntime> _runtimes = new();

    public static TabRuntimeRegistry Shared { get; } = new();

    public TabRuntime GetOrCreate(BrowserTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var runtime = _runtimes.GetValue(
            tab,
            static _ => new TabRuntime());
        tab.BindRuntimePresentation(runtime.Presentation);
        return runtime;
    }

    public bool TryGet(BrowserTab tab, out TabRuntime? runtime)
    {
        ArgumentNullException.ThrowIfNull(tab);
        return _runtimes.TryGetValue(tab, out runtime);
    }
}
