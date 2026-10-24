using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace NativeNCA.Core.Security;

/// <summary>
/// Wraps and unwraps the vault master key with a key that only Windows Hello can unlock.
/// Abstracted so tests can use a software implementation.
/// </summary>
public interface IVaultKeyProtector
{
    bool IsAvailable { get; }

    /// <summary>Creates (or recreates) the protector key. Shows the Windows Hello prompt.</summary>
    void CreateKey(IntPtr ownerWindow);

    /// <summary>Encrypts with the public part. No user interaction.</summary>
    byte[] Wrap(byte[] secret);

    /// <summary>Decrypts. Shows the Windows Hello prompt; throws <see cref="OperationCanceledException"/> if the user cancels.</summary>
    byte[] Unwrap(byte[] wrapped, IntPtr ownerWindow, string reason);

    void DeleteKey();
}

/// <summary>
/// Windows Hello key in the Microsoft Passport key storage provider (TPM-backed where available).
/// The private key never leaves the device and every decryption requires face, fingerprint or PIN.
/// Same approach as KeePassWinHello: RSA-2048 with PKCS#1 padding, driven through ncrypt.dll directly
/// (the managed CngKey API mishandles this provider, e.g. CngKey.Exists throws instead of returning false).
/// </summary>
public sealed class WindowsHelloKeyProtector : IVaultKeyProtector
{
    private const string ProviderName = "Microsoft Passport Key Storage Provider";

    // Passport KSP key names have the form "<user SID>//<domain>/<sub-domain>/<key name>".
    private static string KeyName => $"{WindowsIdentity.GetCurrent().User!.Value}//NativeNCA/Vault/MasterKey";

    public bool IsAvailable
    {
        get
        {
            // The provider opens only when Windows Hello (at least a PIN) is set up for this user.
            if (NCryptOpenStorageProvider(out var provider, ProviderName, 0) != 0) return false;
            NCryptFreeObject(provider);
            return true;
        }
    }

    public void CreateKey(IntPtr ownerWindow)
    {
        DeleteKey();

        Check(NCryptOpenStorageProvider(out var provider, ProviderName, 0), "открыть хранилище ключей Windows Hello");
        try
        {
            Check(NCryptCreatePersistedKey(provider, out var key, "RSA", KeyName, 0, 0), "создать ключ Windows Hello");
            try
            {
                SetInt(key, "Length", 2048);
                SetInt(key, "Key Usage", NcryptAllowAllUsages);
                SetWindow(key, ownerWindow);
                SetString(key, "Use Context", "NativeNCA: создание ключа защиты хранилища");

                // Shows the Windows Hello prompt.
                int status = NCryptFinalizeKey(key, 0);
                if (IsCancellation(status))
                    throw new OperationCanceledException("Создание ключа Windows Hello отменено");
                Check(status, "создать ключ Windows Hello");
            }
            finally
            {
                NCryptFreeObject(key);
            }
        }
        finally
        {
            NCryptFreeObject(provider);
        }
    }

    public byte[] Wrap(byte[] secret)
    {
        // Encrypt with an imported copy of the public key so no Hello UI is involved.
        byte[] publicBlob = WithKey(key =>
        {
            Check(NCryptExportKey(key, IntPtr.Zero, "RSAPUBLICBLOB", IntPtr.Zero, null, 0, out int size, 0), "прочитать открытый ключ");
            var blob = new byte[size];
            Check(NCryptExportKey(key, IntPtr.Zero, "RSAPUBLICBLOB", IntPtr.Zero, blob, size, out size, 0), "прочитать открытый ключ");
            return blob[..size];
        });

        using var publicKey = CngKey.Import(publicBlob, new CngKeyBlobFormat("RSAPUBLICBLOB"));
        using var rsa = new RSACng(publicKey);
        return rsa.Encrypt(secret, RSAEncryptionPadding.Pkcs1);
    }

    public byte[] Unwrap(byte[] wrapped, IntPtr ownerWindow, string reason) => WithKey(key =>
    {
        SetWindow(key, ownerWindow);
        SetString(key, "Use Context", reason);

        // Shows the Windows Hello prompt (the first call already performs the private-key operation).
        var output = new byte[wrapped.Length];
        int status = NCryptDecrypt(key, wrapped, wrapped.Length, IntPtr.Zero, output, output.Length, out int written, NcryptPadPkcs1Flag);
        if (IsCancellation(status))
            throw new OperationCanceledException("Подтверждение Windows Hello отменено");
        Check(status, "расшифровать ключ хранилища");
        return output[..written];
    });

    public void DeleteKey()
    {
        if (NCryptOpenStorageProvider(out var provider, ProviderName, 0) != 0) return;
        try
        {
            if (NCryptOpenKey(provider, out var key, KeyName, 0, 0) == 0)
            {
                // NCryptDeleteKey frees the handle on success.
                if (NCryptDeleteKey(key, 0) != 0) NCryptFreeObject(key);
            }
        }
        finally
        {
            NCryptFreeObject(provider);
        }
    }

    private static T WithKey<T>(Func<IntPtr, T> action)
    {
        Check(NCryptOpenStorageProvider(out var provider, ProviderName, 0), "открыть хранилище ключей Windows Hello");
        try
        {
            int status = NCryptOpenKey(provider, out var key, KeyName, 0, 0);
            if (status is NteNoKey or NteBadKeyset) throw new VaultKeyLostException();
            Check(status, "открыть ключ Windows Hello");
            try
            {
                return action(key);
            }
            finally
            {
                NCryptFreeObject(key);
            }
        }
        finally
        {
            NCryptFreeObject(provider);
        }
    }

    // ---------- ncrypt.dll ----------

    private const int NteNoKey = unchecked((int)0x8009000D);
    private const int NteBadKeyset = unchecked((int)0x80090016);
    private const int NcryptAllowAllUsages = 0x00FFFFFF;
    private const int NcryptPadPkcs1Flag = 0x2;

    // NTE_USER_CANCELLED (0x80090036) / ERROR_CANCELLED (0x800704C7)
    private static bool IsCancellation(int status) =>
        status is unchecked((int)0x80090036) or unchecked((int)0x800704C7);

    private static void Check(int status, string action)
    {
        if (status != 0)
            throw new CryptographicException($"Не удалось {action} (0x{status:X8})") { HResult = status };
    }

    private static void SetInt(IntPtr handle, string property, int value) =>
        Check(NCryptSetProperty(handle, property, BitConverter.GetBytes(value), 4, 0), $"задать свойство «{property}»");

    private static void SetString(IntPtr handle, string property, string value)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(value + "\0");
        Check(NCryptSetProperty(handle, property, bytes, bytes.Length, 0), $"задать свойство «{property}»");
    }

    private static void SetWindow(IntPtr handle, IntPtr window)
    {
        if (window == IntPtr.Zero) return;
        byte[] bytes = IntPtr.Size == 8 ? BitConverter.GetBytes(window.ToInt64()) : BitConverter.GetBytes(window.ToInt32());
        Check(NCryptSetProperty(handle, "HWND Handle", bytes, bytes.Length, 0), "привязать окно Windows Hello");
    }

    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int NCryptOpenStorageProvider(out IntPtr provider, string providerName, int flags);

    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int NCryptOpenKey(IntPtr provider, out IntPtr key, string keyName, int legacyKeySpec, int flags);

    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int NCryptCreatePersistedKey(IntPtr provider, out IntPtr key, string algorithm, string keyName, int legacyKeySpec, int flags);

    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int NCryptSetProperty(IntPtr handle, string property, byte[] input, int inputSize, int flags);

    [DllImport("ncrypt.dll")]
    private static extern int NCryptFinalizeKey(IntPtr key, int flags);

    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int NCryptExportKey(IntPtr key, IntPtr exportKey, string blobType, IntPtr parameterList, byte[]? output, int outputSize, out int result, int flags);

    [DllImport("ncrypt.dll")]
    private static extern int NCryptDecrypt(IntPtr key, byte[] input, int inputSize, IntPtr paddingInfo, byte[] output, int outputSize, out int result, int flags);

    [DllImport("ncrypt.dll")]
    private static extern int NCryptDeleteKey(IntPtr key, int flags);

    [DllImport("ncrypt.dll")]
    private static extern int NCryptFreeObject(IntPtr handle);
}

/// <summary>The Windows Hello key protecting the vault no longer exists (e.g. PIN was reset).</summary>
public class VaultKeyLostException() : Exception(
    "Ключ Windows Hello, защищавший хранилище, больше не существует (например, после сброса PIN-кода). " +
    "Удалите ключи из NativeNCA и добавьте их заново из исходных файлов .p12.");

/// <summary>The vault is protected by Windows Hello and has not been unlocked yet.</summary>
public class VaultLockedException() : Exception("Хранилище заблокировано — требуется подтверждение Windows Hello");
