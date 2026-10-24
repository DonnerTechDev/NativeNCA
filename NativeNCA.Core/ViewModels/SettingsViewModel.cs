using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NativeNCA.Core.Security;
using NativeNCA.Core.Server;
using NativeNCA.Core.Services;
using NativeNCA.Core.Localization;

namespace NativeNCA.Core.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly SecureVaultService _vault;
    private readonly TlsCertificateService? _tls;
    private bool _isLoading;

    public ObservableCollection<KeyItemViewModel> Keys { get; } = new();

    public IReadOnlyList<SessionCacheOption> SessionCacheOptions { get; } =
        new[] { 0, 5, 15, 30, 60 }.Select(m => new SessionCacheOption(m)).ToArray();

    public IReadOnlyList<LanguageOption> LanguageOptions { get; } =
        new[] { AppLanguage.System, AppLanguage.Kazakh, AppLanguage.Russian, AppLanguage.English }
            .Select(l => new LanguageOption(l)).ToArray();

    /// <summary>"Язык": applied at once (all bound texts change) and stored in the vault settings.</summary>
    [ObservableProperty]
    private AppLanguage _language;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedKeyCommand))]
    private KeyItemViewModel? _selectedKey;

    [ObservableProperty]
    private bool _requireWindowsHello;

    [ObservableProperty]
    private bool _autoStartWithWindows;

    [ObservableProperty]
    private int _sessionCacheMinutes;

    [ObservableProperty]
    private bool _useTimestamp;

    /// <summary>Key files and passwords are additionally encrypted with a Windows Hello key.</summary>
    [ObservableProperty]
    private bool _isHelloProtected;

    /// <summary>One-off message for a toast; <see cref="IsStatusError"/> is set before it.</summary>
    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isStatusError;

    /// <summary>Why the local wss server is not running (null when it is).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ServerError))]
    private LocText? _serverErrorText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ServerBannerTitle))]
    private LocText? _serverErrorTitle;

    public string? ServerError => ServerErrorText?.ToString();

    /// <summary>"Запущен NCALayer — сайты не увидят NativeNCA".</summary>
    public string? ServerBannerTitle => ServerErrorTitle is { } title ? Loc.Format(Strings.Server_BannerTitle, title) : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNcaLayerConflict), nameof(IsCertificateError))]
    private ServerErrorKind _serverErrorKind;

    [ObservableProperty]
    private bool _isServerRunning;

    /// <summary>Set by the host application: tries to start the server again.</summary>
    public Action? RetryServerAction { get; set; }

    /// <summary>Set by the host application: restarts the server so it picks up a new certificate.</summary>
    public Action? RestartServerAction { get; set; }

    public bool IsNcaLayerConflict => ServerErrorKind == ServerErrorKind.NcaLayerRunning;

    public bool IsCertificateError => ServerErrorKind == ServerErrorKind.Certificate;

    /// <summary>NCALayer starts with Windows (an enabled Run entry or Startup shortcut was found).</summary>
    [ObservableProperty]
    private bool _isNcaLayerAutostartEnabled;

    /// <summary>Certificate browsers get: own, NCALayer's shared one, or none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOwnTlsCertificate), nameof(TlsStatusText), nameof(TlsDescription))]
    private TlsCertificateSource _tlsSource;

    public bool IsOwnTlsCertificate => TlsSource == TlsCertificateSource.Own;

    public string TlsStatusText => TlsSource switch
    {
        TlsCertificateSource.Own => Strings.Tls_StatusOwn,
        TlsCertificateSource.NcaLayer => Strings.Tls_StatusNcaLayer,
        _ => Strings.Tls_StatusNone
    };

    public string TlsDescription => TlsSource switch
    {
        TlsCertificateSource.Own => Strings.Tls_DescriptionOwn,
        TlsCertificateSource.NcaLayer => Strings.Tls_DescriptionNcaLayer,
        _ => Strings.Tls_DescriptionNone
    };

    /// <summary>Protected vault is currently open (a Windows Hello verification is still valid).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LockVaultCommand))]
    private bool _isVaultUnlocked;

    /// <summary>"Открыто ещё 12 минут" / "Заблокировано". Empty when protection is off.</summary>
    public string VaultStateText
    {
        get
        {
            if (!IsHelloProtected) return string.Empty;
            if (_vault.UnlockedUntil is not { } until) return Strings.Vault_LockedState;
            int minutes = Math.Max(1, (int)Math.Ceiling((until - DateTime.UtcNow).TotalMinutes));
            return Loc.Plural(minutes, Strings.Vault_UnlockedFor_Plural);
        }
    }

    private readonly Timer _lockStateTimer;

    /// <summary>Set by the main window so Windows Hello prompts are parented to it.</summary>
    public IntPtr OwnerWindowHandle { get; set; }

    public bool IsHelloProtectionAvailable => _vault.IsHelloProtectionAvailable;

    [RelayCommand]
    private void RetryServer() => RetryServerAction?.Invoke();

    public bool HasKeys => Keys.Count > 0;

    /// <summary>Why the vault cannot be changed (written by a newer NativeNCA), or null.</summary>
    public string? VaultReadOnlyMessage =>
        _vault.IsReadOnly ? new VaultFormatTooNewException(_vault.FormatVersion).Message : null;

    public SettingsViewModel(SecureVaultService vault, TlsCertificateService? tls = null)
    {
        _vault = vault;
        _tls = tls;
        Keys.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasKeys));
        LoadSettings();
        RefreshKeys();

        // The session expires on its own, so refresh the state periodically as well as on events.
        var context = SynchronizationContext.Current;
        void Refresh() { if (context != null) context.Post(_ => UpdateLockState(), null); else UpdateLockState(); }
        _vault.LockStateChanged += (_, _) => Refresh();
        _lockStateTimer = new Timer(_ => Refresh(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        UpdateLockState();
        RefreshTlsStatus();

        LocalizationSource.Instance.CultureChanged += (_, _) => RefreshTexts();
    }

    /// <summary>The language changed: re-raise every computed text, here and in the key list.</summary>
    private void RefreshTexts()
    {
        foreach (var option in SessionCacheOptions) option.Refresh();
        foreach (var option in LanguageOptions) option.Refresh();
        foreach (var key in Keys) key.RefreshTexts();
        OnPropertyChanged(string.Empty);
    }

    private void UpdateLockState()
    {
        IsVaultUnlocked = _vault.UnlockedUntil != null;
        OnPropertyChanged(nameof(VaultStateText));
    }

    public void RefreshKeys(Guid? selectId = null)
    {
        var keepId = selectId ?? SelectedKey?.Id;

        Keys.Clear();
        foreach (var key in _vault.GetKeys())
        {
            Keys.Add(new KeyItemViewModel(key, _vault.GetAvatarPath(key)));
        }

        SelectedKey = Keys.FirstOrDefault(k => k.Id == keepId) ?? Keys.FirstOrDefault();
    }

    private void LoadSettings()
    {
        _isLoading = true;
        var settings = _vault.GetSettings();
        RequireWindowsHello = settings.RequireWindowsHelloForSigning;
        AutoStartWithWindows = settings.AutoStartWithWindows;
        SessionCacheMinutes = settings.SessionCacheMinutes;
        UseTimestamp = settings.UseTimestamp;
        IsHelloProtected = settings.IsHelloProtected;
        Language = LocalizationSource.FromSetting(settings.Language);
        _isLoading = false;
    }

    // Settings apply immediately, as in Windows 11 Settings — no explicit "Save" button needed.
    partial void OnRequireWindowsHelloChanged(bool value) => AutoSave();
    partial void OnSessionCacheMinutesChanged(int value) => AutoSave();
    partial void OnUseTimestampChanged(bool value) => AutoSave();

    partial void OnLanguageChanged(AppLanguage value)
    {
        if (_isLoading) return;
        AutoSave();
        // After the ComboBox has finished its selection change: the switch re-raises every binding.
        var context = SynchronizationContext.Current;
        if (context != null) context.Post(_ => LocalizationSource.Instance.SetLanguage(value), null);
        else LocalizationSource.Instance.SetLanguage(value);
    }

    partial void OnAutoStartWithWindowsChanged(bool value)
    {
        AutoSave();
        if (!_isLoading) AutoStartService.Apply(value);
    }

    private void AutoSave()
    {
        if (_isLoading) return;
        if (_vault.IsReadOnly)
        {
            ShowError(Strings.Status_SettingsNotSaved);
            return;
        }
        _vault.SaveSettings(new VaultSettings(RequireWindowsHello, AutoStartWithWindows, SessionCacheMinutes, UseTimestamp,
            Language: LocalizationSource.ToSetting(Language)));
    }

    private void ShowStatus(string message)
    {
        IsStatusError = false;
        StatusMessage = message;
    }

    private void ShowError(string message)
    {
        IsStatusError = true;
        StatusMessage = message;
    }

    // ===================== Browser certificate =====================

    public void RefreshTlsStatus()
    {
        if (_tls != null) TlsSource = _tls.GetAvailableSource();
    }

    /// <summary>First run: explain and offer the own certificate, unless it is set up or the user said no.</summary>
    public bool ShouldOfferTlsSetup => _tls != null && TlsSource != TlsCertificateSource.Own && !_tls.IsSetupDeclined;

    public void DeclineTlsSetup()
    {
        if (_tls != null) _tls.IsSetupDeclined = true;
    }

    /// <summary>
    /// Creates the own certificate and adds its root to Windows (Windows asks to confirm).
    /// Throws <see cref="OperationCanceledException"/> if the user declines that prompt.
    /// </summary>
    public async Task InstallTlsCertificateAsync()
    {
        if (_tls == null) return;
        try
        {
            await Task.Run(() => _tls.InstallOwnCertificate(OwnerWindowHandle));
        }
        catch (OperationCanceledException)
        {
            // Do not offer it again on every start; the settings row and the banner still can.
            _tls.IsSetupDeclined = true;
            throw;
        }
        finally
        {
            RefreshTlsStatus();
        }

        RestartServerAction?.Invoke();
        // Firefox reads the Windows roots only at start-up; Chrome and Edge pick the root up at once.
        ShowStatus(Strings.Status_TlsInstalled);
    }

    public async Task RemoveTlsCertificateAsync()
    {
        if (_tls == null) return;
        try
        {
            await Task.Run(() => _tls.RemoveOwnCertificate(OwnerWindowHandle));
        }
        finally
        {
            RefreshTlsStatus();
        }

        RestartServerAction?.Invoke();
        ShowStatus(Strings.Status_TlsRemoved);
    }

    // ===================== Original NCALayer =====================

    public void RefreshNcaLayerAutostart() =>
        IsNcaLayerAutostartEnabled = NcaLayerService.FindAutostartEntries().Count > 0;

    /// <summary>Terminates NCALayer and starts the server on the freed port. False if the port stayed busy.</summary>
    public async Task<bool> CloseNcaLayerAsync()
    {
        bool freed = await NcaLayerService.CloseAsync(NativeNcaServer.WssPort, TimeSpan.FromSeconds(10));
        RetryServerAction?.Invoke();
        return freed;
    }

    /// <summary>Turns NCALayer's autostart off. Returns false if a machine-wide entry needs administrator rights.</summary>
    public bool DisableNcaLayerAutostart()
    {
        var notChanged = NcaLayerService.DisableAutostart(NcaLayerService.FindAutostartEntries());
        RefreshNcaLayerAutostart();
        return notChanged.Count == 0;
    }

    // ===================== Windows Hello vault protection =====================

    /// <summary>One Windows Hello prompt (creating the key), then everything is re-encrypted.</summary>
    public async Task EnableHelloProtectionAsync()
    {
        await _vault.EnableHelloProtectionAsync(OwnerWindowHandle);
        IsHelloProtected = true;
        UpdateLockState();
        _isLoading = true;
        RequireWindowsHello = true;
        _isLoading = false;
        ShowStatus(Strings.Status_HelloEnabled);
    }

    public async Task DisableHelloProtectionAsync()
    {
        await _vault.DisableHelloProtectionAsync(OwnerWindowHandle);
        IsHelloProtected = false;
        UpdateLockState();
        ShowStatus(Strings.Status_HelloDisabled);
    }

    [RelayCommand(CanExecute = nameof(IsVaultUnlocked))]
    private void LockVault()
    {
        _vault.Lock();
        ShowStatus(Strings.Status_VaultLocked);
    }

    private Task UnlockAsync(string reason) => _vault.UnlockAsync(OwnerWindowHandle, reason);

    // ===================== Keys =====================

    private bool CanDeleteSelectedKey() => SelectedKey != null;

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedKey))]
    public void DeleteSelectedKey()
    {
        if (SelectedKey == null) return;

        int index = Keys.IndexOf(SelectedKey);
        _vault.RemoveKey(SelectedKey.Id);
        SelectedKey = null;
        RefreshKeys();

        if (Keys.Count > 0)
        {
            SelectedKey = Keys[Math.Clamp(index, 0, Keys.Count - 1)];
        }

        ShowStatus(Strings.Status_KeyDeleted);
    }

    public async Task AddKeyFromFileAsync(string filePath, string password, string? name, bool savePassword)
    {
        await UnlockAsync(Strings.Hello_AddKey);
        var item = await Task.Run(() => _vault.AddKey(filePath, password, name, savePassword));
        RefreshKeys(item.Id);
        ShowStatus(Strings.Status_KeyAdded);
    }

    public async Task ChangeKeyPasswordAsync(Guid keyId, string currentPassword, string newPassword)
    {
        await UnlockAsync(Strings.Hello_ChangePassword);
        await Task.Run(() => _vault.ChangeKeyPassword(keyId, currentPassword, newPassword));
        RefreshKeys();
        ShowStatus(Strings.Status_PasswordChanged);
    }

    public void SetAvatar(Guid keyId, byte[] png)
    {
        _vault.SetAvatar(keyId, png);
        RefreshKeys();
        ShowStatus(Strings.Status_PhotoUpdated);
    }

    public void RemoveAvatar(Guid keyId)
    {
        _vault.RemoveAvatar(keyId);
        RefreshKeys();
        ShowStatus(Strings.Status_PhotoRemoved);
    }

    public void RenameKey(Guid keyId, string newName)
    {
        var key = _vault.GetKey(keyId);
        if (key == null || string.IsNullOrWhiteSpace(newName) || key.Name == newName) return;

        newName = newName.Trim();
        _vault.UpdateKey(keyId, newName, key.IsPasswordSaved);

        var item = Keys.FirstOrDefault(k => k.Id == keyId);
        if (item != null) item.Name = newName;

        ShowStatus(Strings.Status_KeyRenamed);
    }

    /// <summary>
    /// Enables or disables storing the key password in the vault.
    /// When enabling, the password is verified against the key file first.
    /// </summary>
    public async Task SetPasswordSavedAsync(Guid keyId, bool savePassword, string? password = null)
    {
        var key = _vault.GetKey(keyId) ?? throw new InvalidOperationException(Strings.Error_KeyNotFound);

        if (savePassword)
        {
            await UnlockAsync(Strings.Hello_SavePassword);
            bool valid = !string.IsNullOrEmpty(password) && await Task.Run(() => _vault.VerifyKeyPassword(keyId, password));
            if (!valid)
                throw new InvalidOperationException(Strings.Error_WrongPassword);
        }

        _vault.UpdateKey(keyId, key.Name, savePassword, password);
        RefreshKeys();
        ShowStatus(savePassword ? Strings.Status_PasswordSaved : Strings.Status_PasswordForgotten);
    }
}

/// <summary>"Запоминать подтверждение" choice; its text follows the language.</summary>
public sealed class SessionCacheOption(int minutes) : ObservableObject
{
    public int Minutes { get; } = minutes;

    public string DisplayName => Minutes switch
    {
        0 => Strings.Settings_RememberNever,
        60 => Strings.Settings_RememberHour,
        _ => Loc.Plural(Minutes, Strings.Settings_RememberMinutes_Plural)
    };

    public void Refresh() => OnPropertyChanged(nameof(DisplayName));
}

/// <summary>"Язык" choice. Languages are named in themselves; only "Как в Windows" is translated.</summary>
public sealed class LanguageOption(AppLanguage language) : ObservableObject
{
    public AppLanguage Value { get; } = language;

    public string DisplayName => Value switch
    {
        AppLanguage.Kazakh => Strings.Language_Kazakh,
        AppLanguage.Russian => Strings.Language_Russian,
        AppLanguage.English => Strings.Language_English,
        _ => Strings.Language_System
    };

    public void Refresh() => OnPropertyChanged(nameof(DisplayName));
}
