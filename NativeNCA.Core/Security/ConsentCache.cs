namespace NativeNCA.Core.Security;

/// <summary>
/// Remembers a successful Windows Hello verification for (site, key) for the configured number of
/// minutes, so repeated signing on the same site does not ask for biometrics every time.
/// The user still confirms every operation in the overlay. Kept in memory only.
/// </summary>
public sealed class ConsentCache
{
    private readonly Dictionary<(string Origin, Guid KeyId), DateTime> _entries = new();
    private readonly object _lock = new();

    public bool IsValid(string origin, Guid keyId)
    {
        lock (_lock)
        {
            return _entries.TryGetValue((origin, keyId), out var until) && until > DateTime.UtcNow;
        }
    }

    public void Remember(string origin, Guid keyId, int minutes)
    {
        if (minutes <= 0) return;
        lock (_lock)
        {
            _entries[(origin, keyId)] = DateTime.UtcNow.AddMinutes(minutes);
        }
    }

    public void Clear()
    {
        lock (_lock) _entries.Clear();
    }
}
