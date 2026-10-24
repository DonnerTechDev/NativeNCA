using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NativeNCA.Core.Crypto;
using NativeNCA.Core.Models;

namespace NativeNCA.Core.Security;

/// <summary>
/// Key vault: key containers and (optionally) their passwords are stored encrypted with
/// Windows DPAPI for the current user. Metadata (names, certificate details) lives in vault.json.
///
/// Optional Windows Hello protection adds a second layer: secrets are encrypted with a random master
/// key (AES-256-GCM), and the master key itself is encrypted with a Windows Hello key. Decrypting
/// anything then requires face/fingerprint/PIN; one verification unlocks the vault for the
/// configured session time.
/// </summary>
public class SecureVaultService
{
    // Marks secrets that carry the Windows Hello (master key) layer inside DPAPI.
    private static readonly byte[] HelloEnvelopeMagic = { 0x00, 0x4E, 0x4E, 0x48, 0x01, 0xA5, 0x5A, 0x00 };
    private const int NonceSize = 12, TagSize = 16;

    private readonly string _keysDir;
    private readonly string _avatarsDir;
    private readonly string _dbPath;
    private readonly byte[] _entropy = Encoding.UTF8.GetBytes("NativeNCA.Vault.Entropy.v1");
    private readonly IVaultKeyProtector _protector;
    private VaultDatabase _database = new();
    private readonly object _lock = new();

    private byte[]? _masterKey;
    private DateTime _masterKeyExpiresAt;

    public SecureVaultService(string? customVaultDir = null, IVaultKeyProtector? protector = null)
    {
        _protector = protector ?? new WindowsHelloKeyProtector();
        string vaultDir = customVaultDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NativeNCA", "Vault");
        _keysDir = Path.Combine(vaultDir, "keys");
        _avatarsDir = Path.Combine(vaultDir, "avatars");
        _dbPath = Path.Combine(vaultDir, "vault.json");

        Directory.CreateDirectory(vaultDir);
        Directory.CreateDirectory(_keysDir);
        Directory.CreateDirectory(_avatarsDir);
        LoadDatabase();
    }

    public event EventHandler? Changed;

    /// <summary>Raised when the vault is unlocked with Windows Hello or locked again.</summary>
    public event EventHandler? LockStateChanged;

    /// <summary>When the Windows Hello session ends (null when locked or not protected).</summary>
    public DateTime? UnlockedUntil
    {
        get
        {
            lock (_lock) return IsHelloProtected && _masterKey != null && DateTime.UtcNow < _masterKeyExpiresAt ? _masterKeyExpiresAt : null;
        }
    }

    // ===================== Queries =====================

    public IReadOnlyList<VaultKeyItem> GetKeys()
    {
        lock (_lock)
        {
            return _database.Keys.OrderByDescending(k => k.LastUsedAt ?? k.AddedAt).ToList();
        }
    }

    public VaultKeyItem? GetKey(Guid id)
    {
        lock (_lock)
        {
            return _database.Keys.FirstOrDefault(k => k.Id == id);
        }
    }

    public VaultSettings GetSettings()
    {
        lock (_lock)
        {
            return new VaultSettings(
                _database.RequireWindowsHelloForSigning || IsHelloProtected,
                _database.AutoStartWithWindows,
                _database.SessionCacheMinutes,
                _database.UseTimestamp,
                IsHelloProtected);
        }
    }

    public void SaveSettings(VaultSettings settings)
    {
        lock (_lock)
        {
            _database.RequireWindowsHelloForSigning = settings.RequireWindowsHelloForSigning;
            _database.AutoStartWithWindows = settings.AutoStartWithWindows;
            _database.SessionCacheMinutes = settings.SessionCacheMinutes;
            _database.UseTimestamp = settings.UseTimestamp;
            PersistDatabase();
        }
    }

    // ===================== Windows Hello protection =====================

    public bool IsHelloProtected => _database.HelloWrappedMasterKey != null;

    public bool IsHelloProtectionAvailable => _protector.IsAvailable;

    /// <summary>True when secrets can be decrypted right now without asking for Windows Hello.</summary>
    public bool IsUnlocked
    {
        get
        {
            lock (_lock) return !IsHelloProtected || (_masterKey != null && DateTime.UtcNow < _masterKeyExpiresAt);
        }
    }

    /// <summary>
    /// Asks for Windows Hello (if the vault is protected and locked). Runs on a worker thread: the Hello
    /// UI is parented to <paramref name="ownerWindow"/> and must not block that window's thread.
    /// </summary>
    /// <exception cref="OperationCanceledException">The user cancelled Windows Hello.</exception>
    public Task UnlockAsync(IntPtr ownerWindow, string reason) => IsUnlocked
        ? Task.CompletedTask
        : Task.Run(() =>
        {
            byte[] wrapped;
            lock (_lock) wrapped = Convert.FromBase64String(_database.HelloWrappedMasterKey!);
            byte[] key = _protector.Unwrap(wrapped, ownerWindow, reason);
            if (key.Length != 32) throw new CryptographicException("Повреждён ключ хранилища");
            SetMasterKey(key);
        });

    /// <summary>Forgets the master key: the next operation will ask for Windows Hello again.</summary>
    public void Lock()
    {
        lock (_lock)
        {
            if (_masterKey != null) CryptographicOperations.ZeroMemory(_masterKey);
            _masterKey = null;
        }
        LockStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Creates the Windows Hello key (one Hello prompt) and re-encrypts all key files and saved
    /// passwords under the new master key.
    /// </summary>
    public Task EnableHelloProtectionAsync(IntPtr ownerWindow) => Task.Run(() =>
    {
        if (IsHelloProtected) return;
        if (!_protector.IsAvailable)
            throw new InvalidOperationException("Windows Hello не настроен на этом устройстве");

        _protector.CreateKey(ownerWindow);
        byte[] masterKey = RandomNumberGenerator.GetBytes(32);
        string wrapped = Convert.ToBase64String(_protector.Wrap(masterKey));

        lock (_lock)
        {
            // Read everything with the current (DPAPI-only) layer, then switch layers and write back.
            var plain = ReadAllSecrets();
            SetMasterKeyLocked(masterKey);
            _database.HelloWrappedMasterKey = wrapped;
            WriteAllSecrets(plain);
            PersistDatabase();
        }
    });

    /// <summary>Unlocks (one Hello prompt), then stores everything with DPAPI only and deletes the Hello key.</summary>
    public async Task DisableHelloProtectionAsync(IntPtr ownerWindow)
    {
        if (!IsHelloProtected) return;
        await UnlockAsync(ownerWindow, "NativeNCA: отключение защиты хранилища Windows Hello");

        lock (_lock)
        {
            var plain = ReadAllSecrets();
            _database.HelloWrappedMasterKey = null;
            WriteAllSecrets(plain);
            PersistDatabase();
        }

        Lock();
        _protector.DeleteKey();
    }

    private void SetMasterKey(byte[] key)
    {
        lock (_lock) SetMasterKeyLocked(key);
        LockStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetMasterKeyLocked(byte[] key)
    {
        if (_masterKey != null && !ReferenceEquals(_masterKey, key)) CryptographicOperations.ZeroMemory(_masterKey);
        _masterKey = key;
        // At least a couple of minutes so one operation (and its retries) needs one prompt.
        _masterKeyExpiresAt = DateTime.UtcNow.AddMinutes(Math.Max(2, _database.SessionCacheMinutes));
    }

    private Dictionary<Guid, (byte[] File, string? Password)> ReadAllSecrets() =>
        _database.Keys.ToDictionary(k => k.Id, k => (
            Unprotect(File.ReadAllBytes(Path.Combine(_keysDir, k.EncryptedFileName))),
            k.EncryptedPasswordBase64 == null ? null : Encoding.UTF8.GetString(Unprotect(Convert.FromBase64String(k.EncryptedPasswordBase64)))));

    private void WriteAllSecrets(Dictionary<Guid, (byte[] File, string? Password)> secrets)
    {
        foreach (var key in _database.Keys)
        {
            var (file, password) = secrets[key.Id];
            WriteAtomically(Path.Combine(_keysDir, key.EncryptedFileName), Protect(file));
            key.EncryptedPasswordBase64 = password == null ? null : ProtectPassword(password);
        }
    }

    // ===================== Keys =====================

    /// <summary>Validates the password, reads the certificate and stores the container encrypted.</summary>
    /// <exception cref="WrongPasswordException"/>
    public VaultKeyItem AddKey(string p12FilePath, string password, string? customName = null, bool savePassword = false)
    {
        if (!File.Exists(p12FilePath))
            throw new FileNotFoundException("Файл ключа не найден", p12FilePath);

        byte[] rawBytes = File.ReadAllBytes(p12FilePath);

        // Full load: checks the password, the key/certificate match and that the algorithm is supported.
        var signingKey = SigningKey.Load(rawBytes, password);
        var info = CertificateInfo.From(signingKey.Certificate);

        lock (_lock)
        {
            if (_database.Keys.Any(k => k.CertificateSerial == info.SerialNumberHex && k.Issuer == info.Issuer))
                throw new InvalidOperationException("Этот ключ уже есть в хранилище");
        }

        var keyId = Guid.NewGuid();
        string encFileName = $"{keyId:N}.enc";
        File.WriteAllBytes(Path.Combine(_keysDir, encFileName), Protect(rawBytes));

        string defaultName = !string.IsNullOrWhiteSpace(customName)
            ? customName.Trim()
            : !string.IsNullOrWhiteSpace(info.Organization) ? info.Organization!
            : info.FullName ?? Path.GetFileNameWithoutExtension(p12FilePath);

        var item = new VaultKeyItem
        {
            Id = keyId,
            Name = defaultName,
            OriginalFileName = Path.GetFileName(p12FilePath),
            EncryptedFileName = encFileName,
            Subject = info.Subject,
            Issuer = info.Issuer,
            CertificateSerial = info.SerialNumberHex,
            CertificateBase64 = Convert.ToBase64String(signingKey.Certificate.GetEncoded()),
            Iin = info.Iin,
            Bin = info.Bin,
            FullName = info.FullName,
            Organization = info.Organization,
            NotBefore = info.NotBefore,
            NotAfter = info.NotAfter,
            KeyAlgorithm = info.Algorithm,
            KeyUsage = info.Usage,
            IsPasswordSaved = savePassword,
            EncryptedPasswordBase64 = savePassword ? ProtectPassword(password) : null,
            AddedAt = DateTime.UtcNow
        };

        lock (_lock)
        {
            _database.Keys.Add(item);
            PersistDatabase();
        }

        return item;
    }

    public void UpdateKey(Guid id, string name, bool savePassword, string? newPasswordToSave = null)
    {
        lock (_lock)
        {
            var key = _database.Keys.FirstOrDefault(k => k.Id == id);
            if (key == null) return;

            key.Name = name;
            key.IsPasswordSaved = savePassword;

            if (savePassword && !string.IsNullOrEmpty(newPasswordToSave))
                key.EncryptedPasswordBase64 = ProtectPassword(newPasswordToSave);
            else if (!savePassword)
                key.EncryptedPasswordBase64 = null;

            PersistDatabase();
        }
    }

    // ===================== Avatars =====================

    public const int MaxAvatarBytes = 2 * 1024 * 1024;
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    public string? GetAvatarPath(VaultKeyItem key) =>
        key.AvatarFileName == null ? null : Path.Combine(_avatarsDir, key.AvatarFileName);

    /// <summary>
    /// Stores a cropped PNG picture for the key. Pictures are not secret and are kept unencrypted.
    /// A new file name is used every time so image caches never show the old picture.
    /// </summary>
    public void SetAvatar(Guid id, byte[] png)
    {
        if (png.Length > MaxAvatarBytes || !png.AsSpan().StartsWith(PngSignature))
            throw new ArgumentException("Ожидается изображение PNG размером до 2 МБ", nameof(png));

        lock (_lock)
        {
            var key = _database.Keys.FirstOrDefault(k => k.Id == id) ?? throw new InvalidOperationException("Ключ не найден");

            string fileName = $"{id:N}-{DateTime.UtcNow.Ticks:x}.png";
            WriteAtomically(Path.Combine(_avatarsDir, fileName), png);
            DeleteAvatarFile(key);
            key.AvatarFileName = fileName;
            PersistDatabase();
        }
    }

    public void RemoveAvatar(Guid id)
    {
        lock (_lock)
        {
            var key = _database.Keys.FirstOrDefault(k => k.Id == id);
            if (key?.AvatarFileName == null) return;
            DeleteAvatarFile(key);
            key.AvatarFileName = null;
            PersistDatabase();
        }
    }

    private void DeleteAvatarFile(VaultKeyItem key)
    {
        if (GetAvatarPath(key) is { } path && File.Exists(path))
        {
            try { File.Delete(path); } catch { /* a leftover picture is harmless */ }
        }
    }

    public void RemoveKey(Guid id)
    {
        lock (_lock)
        {
            var key = _database.Keys.FirstOrDefault(k => k.Id == id);
            if (key == null) return;

            DeleteAvatarFile(key);

            string filePath = Path.Combine(_keysDir, key.EncryptedFileName);
            if (File.Exists(filePath))
            {
                try { File.Delete(filePath); } catch { /* the metadata is removed anyway */ }
            }

            _database.Keys.Remove(key);
            PersistDatabase();
        }
    }

    public byte[] DecryptKeyFile(VaultKeyItem key)
    {
        string filePath = Path.Combine(_keysDir, key.EncryptedFileName);
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Файл ключа в защищенном хранилище не найден");

        return Unprotect(File.ReadAllBytes(filePath));
    }

    /// <summary>Decrypts the container and unlocks the private key.</summary>
    /// <exception cref="WrongPasswordException"/>
    public SigningKey OpenKey(VaultKeyItem key, string password) => SigningKey.Load(DecryptKeyFile(key), password);

    public string? GetSavedPassword(VaultKeyItem key)
    {
        if (!key.IsPasswordSaved || string.IsNullOrEmpty(key.EncryptedPasswordBase64))
            return null;

        try
        {
            return Encoding.UTF8.GetString(Unprotect(Convert.FromBase64String(key.EncryptedPasswordBase64)));
        }
        catch
        {
            return null;
        }
    }

    public void MarkKeyUsed(Guid id)
    {
        lock (_lock)
        {
            var key = _database.Keys.FirstOrDefault(k => k.Id == id);
            if (key == null) return;
            key.LastUsedAt = DateTime.UtcNow;
            PersistDatabase();
        }
    }

    public bool VerifyKeyPassword(Guid id, string password)
    {
        var key = GetKey(id);
        if (key == null) return false;

        try
        {
            Pkcs12Reader.Read(DecryptKeyFile(key), password);
            return true;
        }
        catch (WrongPasswordException)
        {
            return false;
        }
    }

    /// <summary>Re-encrypts the stored container with a new password (the original .p12 on disk is untouched).</summary>
    /// <exception cref="WrongPasswordException"/>
    public void ChangeKeyPassword(Guid id, string currentPassword, string newPassword)
    {
        var key = GetKey(id) ?? throw new InvalidOperationException("Ключ не найден");

        byte[] updated = Pkcs12Writer.ChangePassword(DecryptKeyFile(key), currentPassword, newPassword);

        // Make sure the new container opens before replacing the old one.
        SigningKey.Load(updated, newPassword);

        lock (_lock)
        {
            string path = Path.Combine(_keysDir, key.EncryptedFileName);
            WriteAtomically(path, Protect(updated));

            if (key.IsPasswordSaved)
                key.EncryptedPasswordBase64 = ProtectPassword(newPassword);

            PersistDatabase();
        }
    }

    // ===================== Persistence =====================

    private byte[] Protect(byte[] data)
    {
        if (!IsHelloProtected)
            return ProtectedData.Protect(data, _entropy, DataProtectionScope.CurrentUser);

        byte[] key = RequireMasterKey();
        byte[] envelope = new byte[HelloEnvelopeMagic.Length + NonceSize + TagSize + data.Length];
        HelloEnvelopeMagic.CopyTo(envelope, 0);
        var nonce = envelope.AsSpan(HelloEnvelopeMagic.Length, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, data,
            envelope.AsSpan(HelloEnvelopeMagic.Length + NonceSize + TagSize),
            envelope.AsSpan(HelloEnvelopeMagic.Length + NonceSize, TagSize));

        return ProtectedData.Protect(envelope, _entropy, DataProtectionScope.CurrentUser);
    }

    private byte[] Unprotect(byte[] data)
    {
        byte[] inner = ProtectedData.Unprotect(data, _entropy, DataProtectionScope.CurrentUser);
        if (!inner.AsSpan().StartsWith(HelloEnvelopeMagic))
            return inner;

        byte[] key = RequireMasterKey();
        int offset = HelloEnvelopeMagic.Length;
        var plain = new byte[inner.Length - offset - NonceSize - TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(inner.AsSpan(offset, NonceSize), inner.AsSpan(offset + NonceSize + TagSize),
            inner.AsSpan(offset + NonceSize, TagSize), plain);
        return plain;
    }

    private byte[] RequireMasterKey()
    {
        lock (_lock)
        {
            if (_masterKey == null || DateTime.UtcNow >= _masterKeyExpiresAt)
                throw new VaultLockedException();
            return _masterKey;
        }
    }
    private string ProtectPassword(string password) => Convert.ToBase64String(Protect(Encoding.UTF8.GetBytes(password)));

    private void LoadDatabase()
    {
        lock (_lock)
        {
            if (File.Exists(_dbPath))
            {
                try
                {
                    _database = JsonSerializer.Deserialize<VaultDatabase>(File.ReadAllText(_dbPath)) ?? new VaultDatabase();
                }
                catch
                {
                    // Keep a copy of the unreadable file instead of silently losing it.
                    File.Copy(_dbPath, _dbPath + $".broken-{DateTime.Now:yyyyMMddHHmmss}", overwrite: true);
                    _database = new VaultDatabase();
                }
            }
            else
            {
                _database = new VaultDatabase();
                PersistDatabase();
            }

            RefreshCertificateMetadata();
        }
    }

    /// <summary>
    /// Re-derives metadata from the stored public certificate, so rule fixes (e.g. how key purpose
    /// is detected) apply to keys added by older versions without re-importing them.
    /// </summary>
    private void RefreshCertificateMetadata()
    {
        bool changed = false;
        foreach (var key in _database.Keys.Where(k => !string.IsNullOrEmpty(k.CertificateBase64)))
        {
            try
            {
                var cert = new Org.BouncyCastle.X509.X509CertificateParser().ReadCertificate(Convert.FromBase64String(key.CertificateBase64!));
                var info = CertificateInfo.From(cert);
                if (key.KeyUsage != info.Usage || key.Iin != info.Iin || key.Bin != info.Bin)
                {
                    key.KeyUsage = info.Usage;
                    key.Iin = info.Iin;
                    key.Bin = info.Bin;
                    changed = true;
                }
            }
            catch
            {
                // Keep the stored metadata if the certificate cannot be parsed.
            }
        }

        if (changed) PersistDatabase();
    }

    private void PersistDatabase()
    {
        string json = JsonSerializer.Serialize(_database, new JsonSerializerOptions { WriteIndented = true });
        WriteAtomically(_dbPath, Encoding.UTF8.GetBytes(json));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static void WriteAtomically(string path, byte[] content)
    {
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, content);
        File.Move(temp, path, overwrite: true);
    }
}

public sealed record VaultSettings(
    bool RequireWindowsHelloForSigning,
    bool AutoStartWithWindows,
    int SessionCacheMinutes,
    bool UseTimestamp = true,
    bool IsHelloProtected = false);
