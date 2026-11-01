namespace Jellytrim.Core.Services;

/// <summary>Scans and cleanup jobs exclude each other: a sweep must never race deletions and the accounting they change.</summary>
public sealed class MaintenanceGate
{
    private readonly object _lock = new();
    private string? _holder;

    public string? Holder
    {
        get
        {
            lock (_lock)
            {
                return _holder;
            }
        }
    }

    public bool TryEnter(string holder)
    {
        lock (_lock)
        {
            if (_holder is not null)
            {
                return false;
            }

            _holder = holder;
            return true;
        }
    }

    public void Exit(string holder)
    {
        lock (_lock)
        {
            if (_holder == holder)
            {
                _holder = null;
            }
        }
    }
}
