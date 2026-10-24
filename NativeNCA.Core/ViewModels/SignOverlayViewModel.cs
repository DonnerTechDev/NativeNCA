using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NativeNCA.Core.Crypto;
using NativeNCA.Core.Models;
using NativeNCA.Core.Security;

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
    public string OriginDomain => ExtractDomain(_request.Origin);
    public string ActionDescription => _request.Description;
    public string DataPreview => _request.DataToSignPreview;
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
        ErrorMessage = null;
    }

    [RelayCommand]
    public async Task ApproveAsync()
    {
        if (SelectedKey == null)
        {
            ErrorMessage = "Выберите ключ для продолжения";
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
            ErrorMessage = "Введите пароль от ключа";
            return;
        }

        IsProcessing = true;
        ErrorMessage = null;

        try
        {
            string reason = $"NativeNCA: подтвердите использование ключа «{item.Name}» для {OriginDomain}";

            if (_settings.IsHelloProtected)
            {
                // The Hello prompt is the decryption itself: without it the key file cannot be read.
                try
                {
                    await _vault.UnlockAsync(OwnerWindowHandle, reason);
                }
                catch (OperationCanceledException)
                {
                    ErrorMessage = "Подтверждение Windows Hello отменено";
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
                        RequireManualPassword("Windows Hello недоступен — введите пароль от ключа вручную");
                        return;

                    case HelloResult.NotAvailable:
                        // A manually typed password is itself proof of the user's presence.
                        break;

                    case HelloResult.Canceled:
                        ErrorMessage = "Подтверждение Windows Hello отменено";
                        return;

                    default:
                        ErrorMessage = "Не удалось выполнить проверку Windows Hello";
                        return;
                }
            }

            OnPropertyChanged(nameof(WillAskWindowsHello));

            bool usingSavedPassword = !IsPasswordRequired;
            string? password = usingSavedPassword ? _vault.GetSavedPassword(item) : ManualPassword;
            if (password == null)
            {
                RequireManualPassword("Не удалось прочитать сохранённый пароль — введите его вручную");
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
                    RequireManualPassword("Сохранённый пароль не подошёл — введите пароль от ключа");
                else
                {
                    ErrorMessage = "Неверный пароль от ключа";
                    ManualPassword = string.Empty;
                }
                return;
            }

            _tcs.TrySetResult(new SigningPromptResult { Approved = true, SelectedKey = item, Key = key });
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsProcessing = false;
        }
    }

    [RelayCommand]
    public void Cancel() => _tcs.TrySetResult(SigningPromptResult.Cancelled);

    private void RequireManualPassword(string message)
    {
        IsPasswordRequired = true;
        ManualPassword = string.Empty;
        ErrorMessage = message;
    }

    private static string ExtractDomain(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) ? uri.Host : origin;
}
