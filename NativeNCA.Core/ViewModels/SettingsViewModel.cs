using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NativeNCA.Core.Security;
using NativeNCA.Core.Server;
using NativeNCA.Core.Services;

namespace NativeNCA.Core.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly SecureVaultService _vault;
    private readonly TlsCertificateService? _tls;
    private bool _isLoading;

    public ObservableCollection<KeyItemViewModel> Keys { get; } = new();

    public IReadOnlyList<int> SessionCacheOptions { get; } = new[] { 0, 5, 15, 30, 60 };

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

    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>Why the local wss server is not running (null when it is).</summary>
    [ObservableProperty]
    private string? _serverError;

    [ObservableProperty]
    private string? _serverErrorTitle;

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
        TlsCertificateSource.Own => "Установлен",
        TlsCertificateSource.NcaLayer => "Сертификат NCALayer",
        _ => "Не установлен"
    };

    public string TlsDescription => TlsSource switch
    {
        TlsCertificateSource.Own =>
            "Собственный сертификат этого компьютера. Действует только для 127.0.0.1 и localhost, его корневой ключ уничтожен",
        TlsCertificateSource.NcaLayer =>
            "Сейчас используется общий сертификат NCALayer. Установите собственный — так безопаснее, и NCALayer больше не понадобится",
        _ => "Без сертификата браузеры не смогут подключиться к NativeNCA"
    };

    /// <summary>Protected vault is currently open (a Windows Hello verification is still valid).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LockVaultCommand))]
    private bool _isVaultUnlocked;

    /// <summary>"Открыто ещё 12 мин" / "Заблокировано". Empty when protection is off.</summary>
    [ObservableProperty]
    private string _vaultStateText = string.Empty;

    private readonly Timer _lockStateTimer;

    /// <summary>Set by the main window so Windows Hello prompts are parented to it.</summary>
    public IntPtr OwnerWindowHandle { get; set; }

    public bool IsHelloProtectionAvailable => _vault.IsHelloProtectionAvailable;

    [RelayCommand]
    private void RetryServer() => RetryServerAction?.Invoke();

    public bool HasKeys => Keys.Count > 0;

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
    }

    private void UpdateLockState()
    {
        var until = _vault.UnlockedUntil;
        IsVaultUnlocked = until != null;

        if (!IsHelloProtected)
            VaultStateText = string.Empty;
        else if (until is { } u)
            VaultStateText = $"Открыто ещё {Math.Max(1, (int)Math.Ceiling((u - DateTime.UtcNow).TotalMinutes))} мин — подпись пройдёт без запроса Windows Hello";
        else
            VaultStateText = "Заблокировано — для подписи понадобится Windows Hello";
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
        _isLoading = false;
    }

    // Settings apply immediately, as in Windows 11 Settings — no explicit "Save" button needed.
    partial void OnRequireWindowsHelloChanged(bool value) => AutoSave();
    partial void OnSessionCacheMinutesChanged(int value) => AutoSave();
    partial void OnUseTimestampChanged(bool value) => AutoSave();

    partial void OnAutoStartWithWindowsChanged(bool value)
    {
        AutoSave();
        if (!_isLoading) AutoStartService.Apply(value);
    }

    private void AutoSave()
    {
        if (_isLoading) return;
        _vault.SaveSettings(new VaultSettings(RequireWindowsHello, AutoStartWithWindows, SessionCacheMinutes, UseTimestamp));
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
        StatusMessage = "Сертификат установлен. Если Firefox был открыт, перезапустите его";
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
        StatusMessage = "Сертификат NativeNCA удалён из Windows";
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
        StatusMessage = "Хранилище защищено Windows Hello";
    }

    public async Task DisableHelloProtectionAsync()
    {
        await _vault.DisableHelloProtectionAsync(OwnerWindowHandle);
        IsHelloProtected = false;
        UpdateLockState();
        StatusMessage = "Защита Windows Hello отключена — используется только DPAPI";
    }

    [RelayCommand(CanExecute = nameof(IsVaultUnlocked))]
    private void LockVault()
    {
        _vault.Lock();
        StatusMessage = "Хранилище заблокировано";
    }

    private Task UnlockAsync(string reason) => _vault.UnlockAsync(OwnerWindowHandle, $"NativeNCA: {reason}");

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

        StatusMessage = "Ключ удален из хранилища";
    }

    public async Task AddKeyFromFileAsync(string filePath, string password, string? name, bool savePassword)
    {
        await UnlockAsync("добавление ключа в хранилище");
        var item = await Task.Run(() => _vault.AddKey(filePath, password, name, savePassword));
        RefreshKeys(item.Id);
        StatusMessage = "Ключ успешно добавлен в хранилище";
    }

    public async Task ChangeKeyPasswordAsync(Guid keyId, string currentPassword, string newPassword)
    {
        await UnlockAsync("смена пароля ключа");
        await Task.Run(() => _vault.ChangeKeyPassword(keyId, currentPassword, newPassword));
        RefreshKeys();
        StatusMessage = "Пароль ключа успешно изменен";
    }

    public void SetAvatar(Guid keyId, byte[] png)
    {
        _vault.SetAvatar(keyId, png);
        RefreshKeys();
        StatusMessage = "Фото ключа обновлено";
    }

    public void RemoveAvatar(Guid keyId)
    {
        _vault.RemoveAvatar(keyId);
        RefreshKeys();
        StatusMessage = "Фото ключа удалено";
    }

    public void RenameKey(Guid keyId, string newName)
    {
        var key = _vault.GetKey(keyId);
        if (key == null || string.IsNullOrWhiteSpace(newName) || key.Name == newName) return;

        newName = newName.Trim();
        _vault.UpdateKey(keyId, newName, key.IsPasswordSaved);

        var item = Keys.FirstOrDefault(k => k.Id == keyId);
        if (item != null) item.Name = newName;

        StatusMessage = "Название ключа обновлено";
    }

    /// <summary>
    /// Enables or disables storing the key password in the vault.
    /// When enabling, the password is verified against the key file first.
    /// </summary>
    public async Task SetPasswordSavedAsync(Guid keyId, bool savePassword, string? password = null)
    {
        var key = _vault.GetKey(keyId) ?? throw new InvalidOperationException("Ключ не найден");

        if (savePassword)
        {
            await UnlockAsync("сохранение пароля ключа");
            bool valid = !string.IsNullOrEmpty(password) && await Task.Run(() => _vault.VerifyKeyPassword(keyId, password));
            if (!valid)
                throw new InvalidOperationException("Неверный пароль от ключа");
        }

        _vault.UpdateKey(keyId, key.Name, savePassword, password);
        RefreshKeys();
        StatusMessage = savePassword
            ? "Пароль сохранён в защищённом хранилище"
            : "Сохранённый пароль удалён из хранилища";
    }
}
