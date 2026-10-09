namespace TabBrowser.Services;

internal sealed class SplitViewWindowStateCoordinator
{
    private readonly Dictionary<string, int> _accentSlots = new(StringComparer.Ordinal);
    private int _displayRefreshGeneration;
    private bool _isPreparing;

    public bool IsPreparing => _isPreparing;

    public IDisposable EnterPreparation()
    {
        _isPreparing = true;
        return new PreparationLease(this);
    }

    public int BeginDisplayRefresh()
    {
        return ++_displayRefreshGeneration;
    }

    public bool IsCurrentDisplayRefresh(int generation)
    {
        return generation == _displayRefreshGeneration;
    }

    public void RefreshAccentSlots(IEnumerable<string> currentSplitViewIds, int paletteSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(paletteSize);

        var splitViewIds = currentSplitViewIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var currentIds = splitViewIds.ToHashSet(StringComparer.Ordinal);
        foreach (var staleId in _accentSlots.Keys
                     .Where(id => !currentIds.Contains(id))
                     .ToList())
        {
            _accentSlots.Remove(staleId);
        }

        var usedSlots = _accentSlots.Values.ToHashSet();
        foreach (var splitViewId in splitViewIds)
        {
            if (_accentSlots.ContainsKey(splitViewId))
            {
                continue;
            }

            var slot = -1;
            for (var index = 0; index < paletteSize; index++)
            {
                if (!usedSlots.Contains(index))
                {
                    slot = index;
                    break;
                }
            }

            if (slot < 0)
            {
                slot = (StringComparer.Ordinal.GetHashCode(splitViewId) & int.MaxValue)
                    % paletteSize;
            }

            _accentSlots[splitViewId] = slot;
            usedSlots.Add(slot);
        }
    }

    public bool TryGetAccentSlot(string splitViewId, out int slot)
    {
        return _accentSlots.TryGetValue(splitViewId, out slot);
    }

    private void CompletePreparation()
    {
        _isPreparing = false;
    }

    private sealed class PreparationLease(SplitViewWindowStateCoordinator coordinator) : IDisposable
    {
        private SplitViewWindowStateCoordinator? _coordinator = coordinator;

        public void Dispose()
        {
            Interlocked.Exchange(ref _coordinator, null)?.CompletePreparation();
        }
    }
}
