using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NativeNCA.Core.Security;

namespace NativeNCA.Core.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly SecureVaultService _vault;
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
    private bool _isServerRunning;

    /// <summary>Set by the host application: tries to start the server again.</summary>
    public Action? RetryServerAction { get; set; }

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

    public SettingsViewModel(SecureVaultService vault)
    {
        _vault = vault;
        Keys.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasKeys));
        LoadSettings();
        RefreshKeys();

        // The session expires on its own, so refresh the state periodically as well as on events.
        var context = SynchronizationContext.Current;
        void Refresh() { if (context != null) context.Post(_ => UpdateLockState(), null); else UpdateLockState(); }
        _vault.LockStateChanged += (_, _) => Refresh();
        _lockStateTimer = new Timer(_ => Refresh(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        UpdateLockState();
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
