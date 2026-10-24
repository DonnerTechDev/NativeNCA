namespace NativeNCA.Core.Models;

public class VaultKeyItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public string EncryptedFileName { get; set; } = string.Empty;
    
    // Certificate Metadata
    public string Subject { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;
    public string CertificateSerial { get; set; } = string.Empty;
    public string? CertificateBase64 { get; set; }
    public string? Iin { get; set; }
    public string? Bin { get; set; }
    public string? Organization { get; set; }
    public string? FullName { get; set; }
    public DateTime NotBefore { get; set; }
    public DateTime NotAfter { get; set; }
    public string KeyAlgorithm { get; set; } = string.Empty; // "ГОСТ" or "RSA"
    public string KeyUsage { get; set; } = string.Empty; // "AUTH", "SIGN" or "BOTH"
    
    // Security & Password Settings
    public bool IsPasswordSaved { get; set; }
    public string? EncryptedPasswordBase64 { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }

    /// <summary>Square PNG picture chosen by the user (file name inside the vault's avatars folder).</summary>
    public string? AvatarFileName { get; set; }
}

public class VaultDatabase
{
    public List<VaultKeyItem> Keys { get; set; } = new();
    public bool RequireWindowsHelloForSigning { get; set; } = true;
    public bool AutoStartWithWindows { get; set; } = true;
    public int SessionCacheMinutes { get; set; } = 30;

    /// <summary>Add a НУЦ time-stamp (CAdES-T) to CMS signatures.</summary>
    public bool UseTimestamp { get; set; } = true;

    /// <summary>
    /// Vault master key encrypted with the Windows Hello (Microsoft Passport) key. When set, key files and
    /// saved passwords can only be decrypted after Windows Hello verification.
    /// </summary>
    public string? HelloWrappedMasterKey { get; set; }
}
