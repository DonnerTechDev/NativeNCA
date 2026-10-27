using System.Collections.Concurrent;
using NativeNCA.Core.Crypto;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Asn1.X509;

namespace NativeNCA.Core.Security;

public sealed class PendingCsrItem
{
    public required string KeyId { get; init; } // 40 hex chars (SHA-1 of public key)
    public required string Prefix { get; init; } // "GOST512" or "RSA"
    public required KeyAlgorithmKind Kind { get; init; }
    public required PrivateKeyInfo PrivateKeyInfo { get; init; }
    public required SubjectPublicKeyInfo PublicKeyInfo { get; init; }
    public string? Password { get; init; }
    public string? BackupFolder { get; init; }
    public string? CustomName { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Thread-safe in-memory store for newly generated private keys awaiting their certificates
/// from the NCA (between generateCsr and importCertificate calls).
/// </summary>
public sealed class PendingCsrStore
{
    private readonly ConcurrentDictionary<string, PendingCsrItem> _items = new(StringComparer.OrdinalIgnoreCase);

    public void Add(PendingCsrItem item)
    {
        CleanupExpired();
        _items[item.KeyId] = item;
    }

    public PendingCsrItem? Find(string keyId)
    {
        CleanupExpired();
        return _items.TryGetValue(keyId, out var item) ? item : null;
    }

    public bool Remove(string keyId) => _items.TryRemove(keyId, out _);

    public int Count
    {
        get
        {
            CleanupExpired();
            return _items.Count;
        }
    }

    public void CleanupExpired(TimeSpan? maxAge = null)
    {
        var limit = DateTime.UtcNow - (maxAge ?? TimeSpan.FromHours(2));
        foreach (var (key, item) in _items)
        {
            if (item.CreatedAt < limit)
                _items.TryRemove(key, out _);
        }
    }
}
