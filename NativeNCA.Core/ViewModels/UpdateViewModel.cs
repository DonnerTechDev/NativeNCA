using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NativeNCA.Core.Updates;

namespace NativeNCA.Core.ViewModels;

/// <summary>
/// Hands this process over to another NativeNCA exe (after an update, a rollback or an install): stops the
/// server, releases the single-instance mutex and starts <paramref name="exePath"/>. Returns true once the new
/// process has taken over (the host then exits), false if it did not start (the host resumes as before).
/// </summary>
public delegate Task<bool> HandOffHandler(string exePath, string arguments);

/// <summary>Update check, download, swap and rollback, for the main window banner and the settings rows.</summary>
public partial class UpdateViewModel : ObservableObject
{
    public const string UpdatedFromArgument = "--updated-from";
    public const string RolledBackFromArgument = "--rolled-back-from";
    public const string InstalledArgument = "--installed";

    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(8);

    private readonly GitHubReleaseClient? _client;
    private readonly UpdateDownloader? _downloader;
    private readonly UpdateStateStore _store;
    private readonly SynchronizationContext? _context;
    private Timer? _timer;
    private CancellationTokenSource? _downloadCts;
    private bool _showSkipped;

    public UpdateViewModel(AppVersion currentVersion, AppInstallation installation, bool isInstalledCopy,
        UpdateStateStore store, GitHubReleaseClient? client, UpdateDownloader? downloader)
    {
        CurrentVersion = currentVersion;
        Installation = installation;
        IsInstalledCopy = isInstalledCopy;
        _store = store;
        _client = client;
        _downloader = downloader;
        _context = SynchronizationContext.Current;
        _autoCheck = store.Load().AutoCheck;
        RefreshPreviousVersion();
    }

    public AppVersion CurrentVersion { get; }

    public string CurrentVersionText => CurrentVersion.ToString();

    public AppInstallation Installation { get; }

    /// <summary>This process runs from the install folder: only then can it replace itself.</summary>
    public bool IsInstalledCopy { get; }

    /// <summary>Set by the host: hands over to another exe (see <see cref="HandOffHandler"/>).</summary>
    public HandOffHandler? HandOff { get; set; }

    /// <summary>A new version was found that the user has not been notified about (tray notification).</summary>
    public event EventHandler<ReleaseInfo>? NotificationRequested;

    /// <summary>"Обновить" in a copy that is not installed: the host offers to install it.</summary>
    public event EventHandler? InstallRequired;

    // ===================== Check =====================

    [ObservableProperty]
    private bool _autoCheck;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckNowCommand))]
    private bool _isChecking;

    /// <summary>Settings row text under the version: result of the last check.</summary>
    [ObservableProperty]
    private string _checkStatusText = "Обновления ещё не проверялись";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpdateAvailable), nameof(AvailableVersionText), nameof(UpdateBannerTitle),
        nameof(UpdateBannerText), nameof(IsUpdateBannerVisible), nameof(ReleaseNotesUrl))]
    private ReleaseInfo? _availableRelease;

    private ReleasePackage? _availablePackage;

    public bool IsUpdateAvailable => AvailableRelease != null;

    public string AvailableVersionText => AvailableRelease?.Version.ToString() ?? string.Empty;

    public string UpdateBannerTitle => $"Доступна версия {AvailableVersionText}";

    public string UpdateBannerText => IsInstalledCopy
        ? $"У вас версия {CurrentVersion}. Перед установкой NativeNCA проверит подпись разработчика; ключи и параметры сохранятся."
        : $"У вас версия {CurrentVersion}. Эта копия NativeNCA не установлена, поэтому обновить её автоматически нельзя.";

    public string ReleaseNotesUrl => (AvailableRelease?.HtmlUrl ?? GitHubReleaseClient.ReleasesPageUrl).AbsoluteUri;

    public bool IsUpdateBannerVisible => IsUpdateAvailable || IsDownloading;

    /// <summary>Checks at once (after a short delay at start-up) and then every few hours while enabled.</summary>
    public void StartAutomaticChecks()
    {
        _timer?.Dispose();
        _timer = null;
        if (!AutoCheck || _client == null) return;
        _timer = new Timer(_ => Post(() => _ = CheckAsync(manual: false)), null, FirstCheckDelay, CheckInterval);
    }

    partial void OnAutoCheckChanged(bool value)
    {
        _store.Update(s => s.AutoCheck = value);
        StartAutomaticChecks();
    }

    [RelayCommand(CanExecute = nameof(CanCheckNow))]
    private Task CheckNow() => CheckAsync(manual: true);

    private bool CanCheckNow() => !IsChecking && _client != null;

    public async Task CheckAsync(bool manual)
    {
        if (_client == null || IsChecking || IsDownloading) return;
        IsChecking = true;
        if (manual) CheckStatusText = "Проверка обновлений…";

        try
        {
            var release = await _client.GetLatestAsync();
            _store.Update(s => s.LastCheckUtc = DateTime.UtcNow);
            if (manual) _showSkipped = true;
            ApplyCheckResult(release, manual);
        }
        catch (Exception)
        {
            // Unexpected answers (bad JSON etc.) are as quiet as being offline.
            if (manual) CheckStatusText = "Не удалось проверить обновления. Попробуйте позже";
        }
        finally
        {
            IsChecking = false;
        }
    }

    private void ApplyCheckResult(ReleaseInfo? release, bool manual)
    {
        string checkedAt = $"Проверено {DateTime.Now:HH:mm}";

        if (release == null)
        {
            if (_client!.RateLimitedUntil is { } until && until > DateTimeOffset.UtcNow)
                CheckStatusText = $"GitHub временно ограничил проверки. Попробуйте после {until.ToLocalTime():HH:mm}";
            else if (manual || AvailableRelease == null)
                CheckStatusText = "Не удалось проверить обновления — нет подключения к GitHub";
            return;
        }

        if (release.Version <= CurrentVersion)
        {
            AvailableRelease = null;
            _availablePackage = null;
            CheckStatusText = $"Установлена последняя версия · {checkedAt}";
            return;
        }

        var package = GitHubReleaseClient.SelectPackage(release, GitHubReleaseClient.CurrentArchitecture);
        if (package == null)
        {
            // An unsigned or incomplete release is never offered.
            AvailableRelease = null;
            _availablePackage = null;
            CheckStatusText = $"Версия {release.Version} вышла, но ещё не готова для автоматического обновления · {checkedAt}";
            return;
        }

        var state = _store.Load();
        bool skipped = state.SkippedVersion == release.Version.ToString();
        CheckStatusText = skipped && !_showSkipped
            ? $"Доступна версия {release.Version} (вы вернулись с неё на предыдущую) · {checkedAt}"
            : $"Доступна версия {release.Version} · {checkedAt}";

        _availablePackage = package;
        AvailableRelease = skipped && !_showSkipped ? null : release;

        if (!skipped && state.NotifiedVersion != release.Version.ToString())
        {
            _store.Update(s => s.NotifiedVersion = release.Version.ToString());
            NotificationRequested?.Invoke(this, release);
        }
    }

    // ===================== Download and swap =====================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpdateBannerVisible))]
    [NotifyCanExecuteChangedFor(nameof(UpdateNowCommand), nameof(RollBackCommand))]
    private bool _isDownloading;

    /// <summary>0..1 while downloading.</summary>
    [ObservableProperty]
    private double _downloadFraction;

    [ObservableProperty]
    private bool _isDownloadIndeterminate;

    [ObservableProperty]
    private string _downloadText = string.Empty;

    /// <summary>Error from the last update attempt, shown in the banner.</summary>
    [ObservableProperty]
    private string? _updateError;

    [RelayCommand(CanExecute = nameof(CanUpdateNow))]
    private Task UpdateNow() => UpdateAsync();

    private bool CanUpdateNow() => !IsDownloading;

    public async Task UpdateAsync()
    {
        if (!IsInstalledCopy)
        {
            InstallRequired?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (_availablePackage is not { } package || _downloader == null || HandOff == null) return;

        UpdateError = null;
        IsDownloading = true;
        IsDownloadIndeterminate = true;
        DownloadFraction = 0;
        DownloadText = "Подготовка загрузки…";
        _downloadCts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<DownloadProgress>(p =>
            {
                IsDownloadIndeterminate = p.TotalBytes <= 0;
                DownloadFraction = p.Fraction;
                DownloadText = p.TotalBytes > 0
                    ? $"Загрузка {FormatMegabytes(p.BytesReceived)} из {FormatMegabytes(p.TotalBytes)} МБ · {p.Fraction:P0}"
                    : $"Загрузка {FormatMegabytes(p.BytesReceived)} МБ";
            });

            await _downloader.DownloadAndStageAsync(package, CurrentVersion, Installation.NewExePath, progress, _downloadCts.Token);

            IsDownloadIndeterminate = true;
            DownloadText = "Проверка подписи пройдена. Перезапуск NativeNCA…";
            await Task.Run(() => Installation.SwapInNewVersion(CurrentVersion));

            if (!await HandOff(Installation.ExePath, $"{UpdatedFromArgument} {CurrentVersion}"))
            {
                Installation.UndoSwap();
                Installation.CleanUp();
                UpdateError = $"Версия {package.Release.Version} не запустилась. Оставлена текущая версия {CurrentVersion}";
            }
        }
        catch (OperationCanceledException) when (_downloadCts.IsCancellationRequested)
        {
            Installation.CleanUp();
        }
        catch (UpdateRejectedException ex)
        {
            Installation.CleanUp();
            UpdateError = ex.Message;
        }
        catch (Exception ex)
        {
            Installation.CleanUp();
            UpdateError = "Не удалось обновить NativeNCA: " + ex.Message;
        }
        finally
        {
            IsDownloading = false;
            _downloadCts.Dispose();
            _downloadCts = null;
            RefreshPreviousVersion();
        }
    }

    [RelayCommand]
    private void CancelDownload() => _downloadCts?.Cancel();

    private static string FormatMegabytes(long bytes) => (bytes / 1024d / 1024d).ToString("0.0");

    // ===================== After an update / rollback =====================

    /// <summary>Closable banner after <c>--updated-from</c> or <c>--rolled-back-from</c>.</summary>
    [ObservableProperty]
    private string? _restartBannerText;

    [RelayCommand]
    private void DismissRestartBanner() => RestartBannerText = null;

    /// <summary>Reads the hand-over argument this process was started with.</summary>
    public void ApplyStartupArguments(IReadOnlyList<string> args)
    {
        string? updatedFrom = ArgumentValue(args, UpdatedFromArgument);
        string? rolledBackFrom = ArgumentValue(args, RolledBackFromArgument);

        if (updatedFrom != null)
        {
            RestartBannerText = $"NativeNCA обновлён до версии {CurrentVersion}. Если что-то стало работать не так, " +
                                "предыдущую версию можно вернуть в параметрах.";
            _store.Update(s => s.SkippedVersion = null);
        }
        else if (rolledBackFrom != null)
        {
            RestartBannerText = $"Возвращена версия {CurrentVersion}. Версия {rolledBackFrom} больше не будет предлагаться " +
                                "автоматически — её можно установить из параметров кнопкой «Проверить обновления».";
        }
        else if (args.Any(a => string.Equals(a, InstalledArgument, StringComparison.OrdinalIgnoreCase)))
        {
            RestartBannerText = $"NativeNCA {CurrentVersion} установлен. Он есть в меню «Пуск», а обновления будут приходить сами.";
        }
    }

    public static string? ArgumentValue(IReadOnlyList<string> args, string name)
    {
        for (int i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        }
        return null;
    }

    public static bool IsHandOffStart(IReadOnlyList<string> args) =>
        args.Any(a => a.Equals(UpdatedFromArgument, StringComparison.OrdinalIgnoreCase) ||
                      a.Equals(RolledBackFromArgument, StringComparison.OrdinalIgnoreCase) ||
                      a.Equals(InstalledArgument, StringComparison.OrdinalIgnoreCase));

    // ===================== Rollback =====================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviousVersion), nameof(RollbackTitle))]
    private AppVersion? _previousVersion;

    public bool HasPreviousVersion => IsInstalledCopy && PreviousVersion != null;

    public string RollbackTitle => $"Вернуть предыдущую версию ({PreviousVersion})";

    public void RefreshPreviousVersion() => PreviousVersion = IsInstalledCopy ? Installation.GetPreviousVersion() : null;

    /// <summary>Error from the last rollback attempt (shown as a toast by the window).</summary>
    [ObservableProperty]
    private string? _rollbackError;

    [RelayCommand(CanExecute = nameof(CanUpdateNow))]
    private Task RollBack() => RollBackAsync();

    public async Task RollBackAsync()
    {
        if (!IsInstalledCopy || HandOff == null || PreviousVersion is not { } previous) return;

        RollbackError = null;
        try
        {
            await Task.Run(Installation.SwapToPreviousVersion);
            _store.Update(s => s.SkippedVersion = CurrentVersion.ToString());

            if (!await HandOff(Installation.ExePath, $"{RolledBackFromArgument} {CurrentVersion}"))
            {
                Installation.UndoRollback(previous);
                _store.Update(s => s.SkippedVersion = null);
                RollbackError = $"Версия {previous} не запустилась. Оставлена текущая версия {CurrentVersion}";
            }
        }
        catch (Exception ex)
        {
            RollbackError = "Не удалось вернуть предыдущую версию: " + ex.Message;
        }
        finally
        {
            RefreshPreviousVersion();
        }
    }

    private void Post(Action action)
    {
        if (_context != null) _context.Post(_ => action(), null);
        else action();
    }
}
