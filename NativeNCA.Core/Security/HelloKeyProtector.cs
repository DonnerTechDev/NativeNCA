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
/// Same approach as KeePassWinHello: RSA-2048 with PKCS#1 padding.
/// </summary>
public sealed class WindowsHelloKeyProtector : IVaultKeyProtector
{
    private static readonly CngProvider PassportProvider = new("Microsoft Passport Key Storage Provider");

    // Passport KSP key names have the form "<user SID>//<domain>/<sub-domain>/<key name>".
    private static string KeyName => $"{WindowsIdentity.GetCurrent().User!.Value}//NativeNCA/Vault/MasterKey";

    public bool IsAvailable
    {
        get
        {
            try
            {
                // Throws if the provider is unavailable (Windows Hello not set up).
                CngKey.Exists(KeyName, PassportProvider);
                return true;
            }
            catch (CryptographicException)
            {
                return false;
            }
        }
    }

    public void CreateKey(IntPtr ownerWindow)
    {
        if (CngKey.Exists(KeyName, PassportProvider))
            DeleteKey();

        var parameters = new CngKeyCreationParameters
        {
            Provider = PassportProvider,
            KeyUsage = CngKeyUsages.Decryption | CngKeyUsages.Signing,
            ExportPolicy = CngExportPolicies.None,
            ParentWindowHandle = ownerWindow
        };
        parameters.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(2048), CngPropertyOptions.None));
        parameters.Parameters.Add(UseContext("NativeNCA: создание ключа защиты хранилища"));

        try
        {
            using var key = CngKey.Create(CngAlgorithm.Rsa, KeyName, parameters);
        }
        catch (CryptographicException ex) when (IsCancellation(ex))
        {
            throw new OperationCanceledException("Создание ключа Windows Hello отменено", ex);
        }
    }

    public byte[] Wrap(byte[] secret)
    {
        // Encrypt with an imported copy of the public key so no Hello UI is involved.
        using var key = CngKey.Open(KeyName, PassportProvider);
        byte[] publicBlob = key.Export(CngKeyBlobFormat.GenericPublicBlob);
        using var publicKey = CngKey.Import(publicBlob, CngKeyBlobFormat.GenericPublicBlob);
        using var rsa = new RSACng(publicKey);
        return rsa.Encrypt(secret, RSAEncryptionPadding.Pkcs1);
    }

    public byte[] Unwrap(byte[] wrapped, IntPtr ownerWindow, string reason)
    {
        if (!CngKey.Exists(KeyName, PassportProvider))
            throw new VaultKeyLostException();

        using var key = CngKey.Open(KeyName, PassportProvider);
        if (ownerWindow != IntPtr.Zero)
            key.SetProperty(new CngProperty("HWND Handle", BitConverter.GetBytes(ownerWindow.ToInt64()), CngPropertyOptions.None));
        key.SetProperty(UseContext(reason));

        try
        {
            using var rsa = new RSACng(key);
            return rsa.Decrypt(wrapped, RSAEncryptionPadding.Pkcs1);
        }
        catch (CryptographicException ex) when (IsCancellation(ex))
        {
            throw new OperationCanceledException("Подтверждение Windows Hello отменено", ex);
        }
    }

    public void DeleteKey()
    {
        try
        {
            using var key = CngKey.Open(KeyName, PassportProvider);
            key.Delete();
        }
        catch (CryptographicException)
        {
            // Already gone.
        }
    }

    private static CngProperty UseContext(string text) =>
        new("Use Context", Encoding.Unicode.GetBytes(text + "\0"), CngPropertyOptions.None);

    // NTE_USER_CANCELLED (0x80090036) / ERROR_CANCELLED (0x800704C7)
    private static bool IsCancellation(CryptographicException ex) =>
        ex.HResult is unchecked((int)0x80090036) or unchecked((int)0x800704C7);
}

/// <summary>The Windows Hello key protecting the vault no longer exists (e.g. PIN was reset).</summary>
public class VaultKeyLostException() : Exception(
    "Ключ Windows Hello, защищавший хранилище, больше не существует (например, после сброса PIN-кода). " +
    "Удалите ключи из NativeNCA и добавьте их заново из исходных файлов .p12.");

/// <summary>The vault is protected by Windows Hello and has not been unlocked yet.</summary>
public class VaultLockedException() : Exception("Хранилище заблокировано — требуется подтверждение Windows Hello");
