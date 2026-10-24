using CommunityToolkit.Mvvm.ComponentModel;
using NativeNCA.Core.Crypto;
using NativeNCA.Core.Models;
using NativeNCA.Core.Localization;

namespace NativeNCA.Core.ViewModels;

public partial class KeyItemViewModel : ObservableObject
{
    public const int ExpiringSoonDays = 30;

    private readonly VaultKeyItem _model;

    public KeyItemViewModel(VaultKeyItem model, string? avatarPath = null)
    {
        _model = model;
        _name = model.Name;
        AvatarPath = avatarPath;
    }

    /// <summary>Full path of the user's picture for this key, or null to show initials.</summary>
    public string? AvatarPath { get; }
    public bool HasAvatar => AvatarPath != null;

    public VaultKeyItem Model => _model;
    public Guid Id => _model.Id;

    [ObservableProperty]
    private string _name = string.Empty;

    public string FullName => _model.FullName ?? "—";
    public string Iin => _model.Iin ?? "—";
    public string Bin => _model.Bin ?? "—";
    public bool HasBin => !string.IsNullOrWhiteSpace(_model.Bin);
    public string Organization => _model.Organization ?? Strings.Key_Individual;
    public string Issuer => _model.Issuer;
    public string OriginalFileName => _model.OriginalFileName;
    public DateTime NotBefore => _model.NotBefore;
    public DateTime NotAfter => _model.NotAfter;
    public string KeyAlgorithm => _model.KeyAlgorithm;
    public string KeyAlgorithmText => _model.KeyAlgorithm == CertificateInfo.GostAlgorithm ? Strings.Key_Algorithm_Gost : _model.KeyAlgorithm;
    public string KeyUsage => _model.KeyUsage;
    public bool IsAuth => _model.KeyUsage == "AUTH";
    public bool IsAuthAndSign => _model.KeyUsage == "BOTH";
    public string UsageText => _model.KeyUsage switch
    {
        "BOTH" => Strings.Key_Usage_Both,
        "AUTH" => Strings.Key_Usage_Auth,
        _ => Strings.Key_Usage_Sign
    };
    public bool IsPasswordSaved => _model.IsPasswordSaved;

    public bool IsExpired => DateTime.UtcNow > _model.NotAfter;
    public int DaysLeft => Math.Max(0, (int)Math.Ceiling((_model.NotAfter - DateTime.UtcNow).TotalDays));
    public bool IsExpiringSoon => !IsExpired && DaysLeft <= ExpiringSoonDays;

    public string ValidityPeriodText => $"{Loc.Date(_model.NotBefore)} — {Loc.Date(_model.NotAfter)}";

    public string StatusText => IsExpired
        ? Loc.Format(Strings.Key_Status_Expired, Loc.Date(_model.NotAfter))
        : IsExpiringSoon
            ? Loc.Plural(DaysLeft, Strings.Key_Status_ExpiresIn_Plural)
            : Strings.Key_Status_Active;

    public string BadgeText => _model.KeyUsage switch
    {
        "BOTH" => Strings.Key_Purpose_Both,
        "AUTH" => Strings.Key_Purpose_Auth,
        _ => Strings.Key_Purpose_Sign
    };

    /// <summary>The language changed: every text above is computed, so refresh them all.</summary>
    public void RefreshTexts() => OnPropertyChanged(string.Empty);

    public string Initials
    {
        get
        {
            var source = !string.IsNullOrWhiteSpace(_model.FullName) ? _model.FullName : Name;
            var parts = source.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length switch
            {
                0 => "?",
                1 => parts[0][..1].ToUpperInvariant(),
                _ => (parts[0][..1] + parts[1][..1]).ToUpperInvariant()
            };
        }
    }
}
