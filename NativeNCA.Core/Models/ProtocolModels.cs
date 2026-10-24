using NativeNCA.Core.Crypto;

namespace NativeNCA.Core.Models;

public enum KeyUsageFilter
{
    Any,
    Sign,
    Auth
}

/// <summary>What the overlay is asked to do.</summary>
public class SigningPromptRequest
{
    public string Origin { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string DataToSignPreview { get; set; } = string.Empty;
    public KeyUsageFilter RequiredUsage { get; set; } = KeyUsageFilter.Sign;

    /// <summary>false for operations that only reveal the certificate (getKeyInfo): no password, no Hello.</summary>
    public bool RequiresPrivateKey { get; set; } = true;

    /// <summary>Optional filters from basics signerParams.</summary>
    public string? RequiredIin { get; set; }
    public string? RequiredBin { get; set; }
}

public class SigningPromptResult
{
    public bool Approved { get; set; }
    public VaultKeyItem? SelectedKey { get; set; }

    /// <summary>Unlocked key (null when RequiresPrivateKey was false or the prompt was cancelled).</summary>
    public SigningKey? Key { get; set; }

    public static SigningPromptResult Cancelled => new() { Approved = false };
}
