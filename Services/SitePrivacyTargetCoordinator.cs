namespace TabBrowser.Services;

// UIスレッドで使う対象管理。切替後も実行中の操作が所有する資源は解放しない。
internal sealed class SitePrivacyTargetCoordinator<T> : IDisposable where T : class
{
    private Target? _current;
    private bool _disposed;
    public int Generation { get; private set; }

    public int BeginLoad()
    {
        Generation++;
        _current?.Release();
        _current = null;
        return Generation;
    }

    public bool SetTarget(int generation, string containerId, T value, Action dispose)
    {
        if (_disposed || generation != Generation)
        {
            dispose();
            return false;
        }

        _current = new Target(generation, containerId, value, dispose);
        return true;
    }

    public Lease? Acquire(int generation, string containerId)
    {
        if (_disposed || _current is not { } target || generation != target.Generation
            || !string.Equals(containerId, target.ContainerId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        target.References++;
        return new Lease(target);
    }

    public bool IsCurrent(Lease lease) => !_disposed && ReferenceEquals(_current, lease.TargetValue);

    public void Dispose()
    {
        _disposed = true;
        BeginLoad();
    }

    internal sealed class Target(int generation, string containerId, T value, Action dispose)
    {
        public int Generation { get; } = generation;
        public string ContainerId { get; } = containerId;
        public T Value { get; } = value;
        public int References { get; set; } = 1;
        public void Release()
        {
            if (--References == 0) dispose();
        }
    }

    internal sealed class Lease(Target target) : IDisposable
    {
        private bool _disposed;
        internal Target TargetValue { get; } = target;
        public T Value => TargetValue.Value;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            TargetValue.Release();
        }
    }
}
