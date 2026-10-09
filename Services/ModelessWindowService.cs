using System.Diagnostics.CodeAnalysis;
using System.Windows;

namespace TabBrowser.Services;

public readonly record struct ModelessWindowDiagnosticSnapshot(
    int RegisteredWindowCount,
    int StaleWindowCount,
    int ClosedOwnerWindowCount);

internal static class ModelessWindowService
{
    private static readonly List<Window> OpenWindows = [];
    private static readonly HashSet<Window> ForcedTopmostWindows = [];

    public static void Show(Window window, bool forceTopmost = false)
    {
        _ = ShowAsync(window, forceTopmost);
    }

    public static Task<bool> ShowAsync(Window window, bool forceTopmost = false, bool allowMultiple = false)
    {
        ArgumentNullException.ThrowIfNull(window);

        var existing = allowMultiple ? null : OpenWindows.FirstOrDefault(candidate =>
            candidate.IsLoaded
            && candidate.GetType() == window.GetType()
            && ReferenceEquals(candidate.Owner, window.Owner));
        if (existing is not null)
        {
            if (forceTopmost)
            {
                ForcedTopmostWindows.Add(existing);
            }

            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            ApplyOwnerTopmost(existing);
            existing.Activate();
            return Task.FromResult(false);
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void HandleClosed(object? sender, EventArgs e)
        {
            window.Closed -= HandleClosed;
            OpenWindows.Remove(window);
            ForcedTopmostWindows.Remove(window);
            completion.TrySetResult(true);
        }

        window.Closed += HandleClosed;
        OpenWindows.Add(window);
        if (forceTopmost)
        {
            ForcedTopmostWindows.Add(window);
        }

        try
        {
            ApplyOwnerTopmost(window);
            window.Show();
            ApplyOwnerTopmost(window);
        }
        catch
        {
            window.Closed -= HandleClosed;
            OpenWindows.Remove(window);
            ForcedTopmostWindows.Remove(window);
            throw;
        }

        return completion.Task;
    }

    public static bool TryGetOwned<TWindow>(
        Window owner,
        [NotNullWhen(true)] out TWindow? window)
        where TWindow : Window
    {
        ArgumentNullException.ThrowIfNull(owner);

        window = OpenWindows.FirstOrDefault(candidate =>
                candidate.IsLoaded
                && candidate.GetType() == typeof(TWindow)
                && ReferenceEquals(candidate.Owner, owner))
            as TWindow;
        return window is not null;
    }

    public static bool CloseOwned<TWindow>(Window owner)
        where TWindow : Window
    {
        if (!TryGetOwned<TWindow>(owner, out var window))
        {
            return false;
        }

        window.Close();
        return true;
    }

    public static void SynchronizeOwnedWindowTopmost(Window owner, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(owner);

        foreach (var window in OpenWindows.Where(window => IsOwnedBy(window, owner)).ToList())
        {
            WindowTopmostService.Apply(window, enabled || ForcedTopmostWindows.Contains(window));
        }
    }

    public static ModelessWindowDiagnosticSnapshot GetDiagnosticSnapshot()
    {
        return new ModelessWindowDiagnosticSnapshot(
            OpenWindows.Count,
            OpenWindows.Count(static window =>
                !window.IsLoaded
                || window.Dispatcher.HasShutdownStarted
                || window.Dispatcher.HasShutdownFinished),
            OpenWindows.Count(static window =>
                window.Owner is { IsLoaded: false }));
    }

    private static void ApplyOwnerTopmost(Window window)
    {
        WindowTopmostService.Apply(
            window,
            ForcedTopmostWindows.Contains(window) || window.Owner?.Topmost == true);
    }

    private static bool IsOwnedBy(Window window, Window owner)
    {
        for (var current = window.Owner; current is not null; current = current.Owner)
        {
            if (ReferenceEquals(current, owner))
            {
                return true;
            }
        }

        return false;
    }
}
