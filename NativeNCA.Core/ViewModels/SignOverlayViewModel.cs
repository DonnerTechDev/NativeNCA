using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NativeNCA.Core.Crypto;
using NativeNCA.Core.Models;
using NativeNCA.Core.Security;
using NativeNCA.Core.Localization;

namespace NativeNCA.Core.ViewModels;

public partial class SignOverlayViewModel : ObservableObject
{
    private readonly SecureVaultService _vault;
    private readonly ConsentCache _consentCache;
    private readonly SigningPromptRequest _request;
    private readonly VaultSettings _settings;
    private readonly TaskCompletionSource<SigningPromptResult> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<SigningPromptResult> Task => _tcs.Task;

    public ObservableCollection<KeyItemViewModel> MatchingKeys { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WillAskWindowsHello))]
    private KeyItemViewModel? _selectedKey;

    [ObservableProperty]
    private string _manualPassword = string.Empty;

    [ObservableProperty]
    private bool _isPasswordRequired;

    [ObservableProperty]
    private bool _isProcessing;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Set by the window so the Windows Hello prompt is parented to the overlay.</summary>
    public IntPtr OwnerWindowHandle { get; set; }

    public string OriginUrl => _request.Origin;
    public string OriginDomain => string.IsNullOrEmpty(_request.Origin) ? Strings.Overlay_UnknownOrigin : ExtractDomain(_request.Origin);
    public string ActionDescription => _request.Description.ToString();
    public string DataPreview => _request.DataToSignPreview.ToString();
    public bool IsAuthRequest => _request.RequiredUsage == KeyUsageFilter.Auth;
    public bool IsSelectionOnly => !_request.RequiresPrivateKey;
    public bool IsSecureOrigin => _request.Origin.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    public bool IsWindowsHelloRequired => _settings.RequireWindowsHelloForSigning && !IsSelectionOnly;
    public bool HasKeys => MatchingKeys.Count > 0;

    /// <summary>True when pressing the main button will show the Windows Hello prompt.</summary>
    public bool WillAskWindowsHello => !IsSelectionOnly && (_settings.IsHelloProtected
        ? !_vault.IsUnlocked
        : IsWindowsHelloRequired && !(SelectedKey != null && _consentCache.IsValid(_request.Origin, SelectedKey.Id)));

    public SignOverlayViewModel(SecureVaultService vault, ConsentCache consentCache, SigningPromptRequest request)
    {
        _vault = vault;
        _consentCache = consentCache;
        _request = request;
        _settings = vault.GetSettings();

        LoadKeys();

        LocalizationSource.Instance.CultureChanged += OnCultureChanged;
        _tcs.Task.ContinueWith(_ => LocalizationSource.Instance.CultureChanged -= OnCultureChanged, TaskScheduler.Default);
    }

    /// <summary>Error messages are kept as <see cref="LocText"/> so they follow a language switch too.</summary>
    private LocText? _error;

    private void SetError(LocText? error)
    {
        _error = error;
        ErrorMessage = error?.ToString();
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        foreach (var key in MatchingKeys) key.RefreshTexts();
        if (_error != null) ErrorMessage = _error.ToString();
        OnPropertyChanged(string.Empty);
    }

    private void LoadKeys()
    {
        var keys = _vault.GetKeys()
            .Where(k => k.NotAfter > DateTime.UtcNow)
            .Where(k => _request.RequiredUsage switch
            {
                KeyUsageFilter.Sign => k.KeyUsage is "SIGN" or "BOTH",
                KeyUsageFilter.Auth => k.KeyUsage is "AUTH" or "BOTH",
                _ => true
            })
            .Where(k => string.IsNullOrEmpty(_request.RequiredIin) || k.Iin == _request.RequiredIin)
            .Where(k => string.IsNullOrEmpty(_request.RequiredBin) || k.Bin == _request.RequiredBin)
            .OrderByDescending(k => k.LastUsedAt ?? k.AddedAt);

        MatchingKeys.Clear();
        foreach (var k in keys)
            MatchingKeys.Add(new KeyItemViewModel(k, _vault.GetAvatarPath(k)));

        SelectedKey = MatchingKeys.FirstOrDefault();
        OnPropertyChanged(nameof(HasKeys));
    }

    partial void OnSelectedKeyChanged(KeyItemViewModel? value)
    {
        IsPasswordRequired = value != null && !IsSelectionOnly && !value.IsPasswordSaved;
        ManualPassword = string.Empty;
        SetError(null);
    }

    [RelayCommand]
    public async Task ApproveAsync()
    {
        if (SelectedKey == null)
        {
            SetError(new LocText(() => Strings.Overlay_SelectKey));
            return;
        }

        var item = SelectedKey.Model;

        if (IsSelectionOnly)
        {
            _tcs.TrySetResult(new SigningPromptResult { Approved = true, SelectedKey = item });
            return;
        }

        if (IsPasswordRequired && string.IsNullOrEmpty(ManualPassword))
        {
            SetError(new LocText(() => Strings.Overlay_EnterPassword));
            return;
        }

        IsProcessing = true;
        SetError(null);

        try
        {
            string reason = Loc.Format(Strings.Hello_ConfirmKeyUse, item.Name, OriginDomain);

            if (_settings.IsHelloProtected)
            {
                // The Hello prompt is the decryption itself: without it the key file cannot be read.
                try
                {
                    await _vault.UnlockAsync(OwnerWindowHandle, reason);
                }
                catch (OperationCanceledException)
                {
                    SetError(new LocText(() => Strings.Hello_Canceled));
                    return;
                }
            }
            else if (WillAskWindowsHello)
            {
                switch (await WindowsHelloService.RequestConsentAsync(reason, OwnerWindowHandle))
                {
                    case HelloResult.Verified:
                        _consentCache.Remember(_request.Origin, item.Id, _settings.SessionCacheMinutes);
                        break;

                    case HelloResult.NotAvailable when !IsPasswordRequired:
                        // Never let a stored password be used without any user verification.
                        RequireManualPassword(new LocText(() => Strings.Overlay_HelloUnavailable));
                        return;

                    case HelloResult.NotAvailable:
                        // A manually typed password is itself proof of the user's presence.
                        break;

                    case HelloResult.Canceled:
                        SetError(new LocText(() => Strings.Hello_Canceled));
                        return;

                    default:
                        SetError(new LocText(() => Strings.Overlay_HelloFailed));
                        return;
                }
            }

            OnPropertyChanged(nameof(WillAskWindowsHello));

            bool usingSavedPassword = !IsPasswordRequired;
            string? password = usingSavedPassword ? _vault.GetSavedPassword(item) : ManualPassword;
            if (password == null)
            {
                RequireManualPassword(new LocText(() => Strings.Overlay_SavedPasswordUnreadable));
                return;
            }

            SigningKey key;
            try
            {
                key = await System.Threading.Tasks.Task.Run(() => _vault.OpenKey(item, password));
            }
            catch (WrongPasswordException)
            {
                if (usingSavedPassword)
                    RequireManualPassword(new LocText(() => Strings.Overlay_SavedPasswordWrong));
                else
                {
                    SetError(new LocText(() => Strings.Error_WrongPassword));
                    ManualPassword = string.Empty;
                }
                return;
            }

            _tcs.TrySetResult(new SigningPromptResult { Approved = true, SelectedKey = item, Key = key });
        }
        catch (Exception ex)
        {
            SetError(LocText.Raw(ex.Message));
        }
        finally
        {
            IsProcessing = false;
        }
    }

    [RelayCommand]
    public void Cancel() => _tcs.TrySetResult(SigningPromptResult.Cancelled);

    private void RequireManualPassword(LocText message)
    {
        IsPasswordRequired = true;
        ManualPassword = string.Empty;
        SetError(message);
    }

    private static string ExtractDomain(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) ? uri.Host : origin;
}
