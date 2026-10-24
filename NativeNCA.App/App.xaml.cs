using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32.SafeHandles;
using NativeNCA.App.Services;
using NativeNCA.App.Views;
using NativeNCA.Core.Security;
using NativeNCA.Core.Server;
using NativeNCA.Core.Updates;
using NativeNCA.Core.ViewModels;
using Wpf.Ui.Appearance;

namespace NativeNCA.App;

public partial class App : Application
{
    /// <summary>Never offer installation (also: a file named <c>portable</c> next to the exe).</summary>
    private const string PortableArgument = "--portable";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showEvent;
    private EventWaitHandle? _exitEvent;
    private NativeNcaServer? _server;
    private MainWindow? _mainWindow;

    private readonly AppVersion _version = AppVersion.FromAssembly(typeof(App).Assembly);
    private readonly AppInstallation _installation = new();

    private async void Application_Startup(object sender, StartupEventArgs e)
    {
        var args = e.Args;
        // Follow the Windows light/dark theme and accent colour instead of a hard-coded theme.
        ApplicationThemeManager.ApplySystemTheme(true);

        if (HasArgument(args, SetupService.UninstallArgument))
        {
            RunUninstall();
            return;
        }

        bool isInstalledCopy = _installation.IsInstalledCopy(Environment.ProcessPath);
        bool isHandOff = UpdateViewModel.IsHandOffStart(args);
        bool startInBackground = HasArgument(args, AutoStartService.BackgroundArgument);

        // Started from Downloads or a zip: offer to install instead of running from there.
        if (!isInstalledCopy && !isHandOff && !startInBackground && !IsPortable(args))
        {
            var choice = await ShowInstallWindowAsync();
            if (choice != SetupResult.RunWithoutInstalling)
            {
                Shutdown();
                return;
            }
        }

        if (!AcquireInstanceMutex(waitForPrevious: isHandOff))
        {
            // Only one NativeNCA may own port 13579: a second launch just brings the first one forward.
            InstanceSignals.ShowRunningInstance();
            Shutdown();
            return;
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceSignals.ShowEventName);
        _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceSignals.ExitEventName);
        ListenForSignals();

        // The Run entry belongs to the installed copy, even while a copy from elsewhere is running.
        AutoStartService.ExecutablePath = _installation.IsInstalled ? _installation.ExePath : Environment.ProcessPath;
        if (isInstalledCopy) RefreshInstallation();

        var vault = new SecureVaultService();
        var promptService = new WpfUserPromptService(vault, new ConsentCache());
        var tls = new TlsCertificateService();
        _server = new NativeNcaServer(vault, tls, promptService);

        var settingsVm = new SettingsViewModel(vault, tls)
        {
            RetryServerAction = () => _server.Start(),
            RestartServerAction = () => _server.Restart()
        };

        _server.StateChanged += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            settingsVm.IsServerRunning = _server.IsRunning;
            settingsVm.ServerError = _server.StartError;
            settingsVm.ServerErrorTitle = _server.StartErrorTitle;
            settingsVm.ServerErrorKind = _server.StartErrorKind;
            if (_server.StartErrorKind == ServerErrorKind.NcaLayerRunning)
                settingsVm.RefreshNcaLayerAutostart();
        });
        // Server problems are shown as a banner in the main window, so bring it up.
        promptService.NotificationRequested += (_, _) => ShowMainWindow();

        var updateVm = CreateUpdateViewModel(isInstalledCopy);
        updateVm.ApplyStartupArguments(args);

        _mainWindow = new MainWindow(settingsVm, updateVm);
        _mainWindow.ExitRequested += (_, _) => ExitApplication();
        _mainWindow.InstallRequested += async (_, _) => await InstallFromRunningCopyAsync();
        _mainWindow.UninstallRequested += (_, _) => StartUninstaller();

        if (startInBackground)
            _mainWindow.InitializeHidden();
        else
            _mainWindow.Show();

        await StartServerAsync(retry: isHandOff);
        if (isHandOff) InstanceSignals.SignalHandOffReady();

        // Keep the Run entry pointing at the current exe (e.g. after the app was moved or updated).
        AutoStartService.Apply(vault.GetSettings().AutoStartWithWindows);

        updateVm.StartAutomaticChecks();
    }

    private static bool HasArgument(string[] args, string name) => args.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static bool IsPortable(string[] args)
    {
#if DEBUG
        // Debug builds run from bin\ and never offer to install themselves.
        return true;
#else
        string? dir = Path.GetDirectoryName(Environment.ProcessPath);
        return HasArgument(args, PortableArgument) || (dir != null && File.Exists(Path.Combine(dir, "portable")));
#endif
    }

    /// <summary>
    /// Takes the single-instance mutex. After a hand-over the previous process is still shutting down, so
    /// wait for it instead of signalling it to show its window.
    /// </summary>
    private bool AcquireInstanceMutex(bool waitForPrevious)
    {
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceSignals.MutexName, out bool isFirstInstance);
        if (isFirstInstance) return true;

        if (waitForPrevious)
        {
            try
            {
                if (_instanceMutex.WaitOne(TimeSpan.FromSeconds(30))) return true;
            }
            catch (AbandonedMutexException)
            {
                return true; // the previous owner exited without releasing it: it is ours now
            }
        }

        _instanceMutex.Dispose();
        _instanceMutex = null;
        return false;
    }

    private void ListenForSignals()
    {
        var show = _showEvent!;
        var exit = _exitEvent!;
        new Thread(() =>
        {
            while (true)
            {
                int signal;
                try { signal = WaitHandle.WaitAny(new WaitHandle[] { show, exit }); }
                catch (ObjectDisposedException) { return; }

                if (signal == 0) Dispatcher.BeginInvoke(ShowMainWindow);
                else
                {
                    // Another NativeNCA (installer, uninstaller) needs the port and the files.
                    Dispatcher.BeginInvoke(ExitApplication);
                    return;
                }
            }
        }) { IsBackground = true, Name = "NativeNCA.Signals" }.Start();
    }

    /// <summary>After a hand-over the old process may still be closing its listener: retry for a few seconds.</summary>
    private async Task StartServerAsync(bool retry)
    {
        _server!.Start();
        for (int i = 0; retry && !_server.IsRunning && _server.StartErrorKind != ServerErrorKind.Certificate && i < 10; i++)
        {
            await Task.Delay(500);
            _server.Start();
        }
    }

    private void ShowMainWindow() => _mainWindow?.BringToFront();

    private void ExitApplication()
    {
        _server?.Stop();
        _mainWindow?.CloseForExit();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _server?.Stop();
        _showEvent?.Dispose();
        _exitEvent?.Dispose();
        ReleaseInstanceMutex();
        base.OnExit(e);
    }

    private void ReleaseInstanceMutex()
    {
        if (_instanceMutex == null) return;
        try { _instanceMutex.ReleaseMutex(); } catch (ApplicationException) { /* not owned */ }
        _instanceMutex.Dispose();
        _instanceMutex = null;
    }

    // ===================== Install =====================

    /// <summary>The installed copy keeps "Installed apps" in step with its version and removes update leftovers.</summary>
    private void RefreshInstallation()
    {
        _installation.CleanUp();
        try
        {
            if (SetupService.IsRegistered() && SetupService.GetRegisteredVersion() != _version.ToString())
                new SetupService(_installation).Register(_version);
        }
        catch
        {
            // Cosmetic: the entry still uninstalls correctly.
        }
    }

    private async Task<SetupResult> ShowInstallWindowAsync()
    {
        var setup = new SetupService(_installation);
        var window = SetupWindow.ForInstall(_version, _installation.GetInstalledVersion(),
            install: async () =>
            {
                if (!await InstanceSignals.CloseRunningInstanceAsync(TimeSpan.FromSeconds(15)))
                    throw new InvalidOperationException("Не удалось закрыть запущенный NativeNCA. Закройте его через значок в трее и повторите");
                await Task.Run(() => setup.Install(Environment.ProcessPath!, _version));
                StartProcess(_installation.ExePath, UpdateViewModel.InstalledArgument);
            },
            openInstalled: () =>
            {
                if (InstanceSignals.IsInstanceRunning()) InstanceSignals.ShowRunningInstance();
                else StartProcess(_installation.ExePath, "");
            });

        var closed = new TaskCompletionSource();
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show();
        window.Activate();
        await closed.Task;
        return window.Result;
    }

    /// <summary>"Установить" from a running copy that is not installed (offered instead of updating it).</summary>
    private async Task InstallFromRunningCopyAsync()
    {
        try
        {
            await Task.Run(() => new SetupService(_installation).Install(Environment.ProcessPath!, _version));
        }
        catch (Exception ex)
        {
            _mainWindow?.ShowError("Не удалось установить NativeNCA: " + ex.Message);
            return;
        }

        if (!await HandOffAsync(_installation.ExePath, UpdateViewModel.InstalledArgument))
            _mainWindow?.ShowError("NativeNCA установлен, но не запустился. Запустите его из меню «Пуск»");
    }

    private static void StartProcess(string exePath, string arguments) =>
        Process.Start(new ProcessStartInfo(exePath, arguments)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exePath)!
        })?.Dispose();

    // ===================== Uninstall =====================

    /// <summary>Settings → "Удалить NativeNCA": the uninstaller runs as its own process and closes this one.</summary>
    private void StartUninstaller()
    {
        string exe = _installation.IsInstalled ? _installation.ExePath : Environment.ProcessPath!;
        try
        {
            StartProcess(exe, SetupService.UninstallArgument);
        }
        catch (Exception ex)
        {
            _mainWindow?.ShowError("Не удалось запустить удаление: " + ex.Message);
        }
    }

    private void RunUninstall()
    {
        var setup = new SetupService(_installation);
        SetupWindow? window = null;
        window = SetupWindow.ForUninstall(_installation.GetInstalledVersion() ?? _version, async removeData =>
        {
            if (!await InstanceSignals.CloseRunningInstanceAsync(TimeSpan.FromSeconds(15)))
                throw new InvalidOperationException("Не удалось закрыть запущенный NativeNCA. Закройте его через значок в трее и повторите");

            setup.Unregister();

            string? warning = null;
            if (removeData)
            {
                var owner = new System.Windows.Interop.WindowInteropHelper(window!).Handle;
                try
                {
                    await Task.Run(() => new TlsCertificateService().RemoveOwnCertificate(owner));
                }
                catch (OperationCanceledException)
                {
                    warning = "Корневой сертификат NativeNCA остался в Windows: в окне подтверждения был выбран ответ «Нет». " +
                              "Его можно удалить в оснастке certmgr.msc, раздел «Доверенные корневые центры сертификации».";
                }

                try { new WindowsHelloKeyProtector().DeleteKey(); } catch { /* no Hello key */ }

                try
                {
                    SetupService.DeleteUserData();
                }
                catch (Exception ex)
                {
                    warning = $"Часть данных удалить не удалось ({ex.Message}). Удалите папку {SetupService.UserDataDirectory} вручную.";
                }
            }

            return warning;
        });

        window.Closed += (_, _) =>
        {
            // The folder can only go once this exe (often the installed one) has exited.
            if (window.Result == SetupResult.Uninstalled)
            {
                try { setup.ScheduleDirectoryRemoval(); } catch { /* left for the user to delete */ }
            }
            Shutdown();
        };
        window.Show();
        window.Activate();
    }

    // ===================== Update and rollback =====================

    private UpdateViewModel CreateUpdateViewModel(bool isInstalledCopy)
    {
        var store = new UpdateStateStore();
        var api = GitHubReleaseClient.CreateHttpClient(_version);
        var downloads = GitHubReleaseClient.CreateHttpClient(_version, Timeout.InfiniteTimeSpan);
        string downloadDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NativeNCA", "Updates");

        return new UpdateViewModel(_version, _installation, isInstalledCopy, store,
            new GitHubReleaseClient(api), new UpdateDownloader(downloads, downloadDir))
        {
            HandOff = HandOffAsync
        };
    }

    /// <summary>
    /// Stops serving, releases the single-instance mutex and starts <paramref name="exePath"/>. Waits until the new
    /// process reports that it serves (or, for versions that cannot report it, is still alive after a while).
    /// On success this process exits; otherwise it takes the mutex and the port back and keeps running.
    /// </summary>
    private async Task<bool> HandOffAsync(string exePath, string arguments)
    {
        bool wasVisible = _mainWindow?.IsVisible == true;
        if (!wasVisible) arguments += " " + AutoStartService.BackgroundArgument;

        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, InstanceSignals.HandOffReadyEventName);
        ready.Reset();

        _server?.Stop();
        ReleaseInstanceMutex();

        bool tookOver = false;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(exePath, arguments)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exePath)!
            });

            if (process != null)
            {
                _mainWindow?.Hide();
                tookOver = await Task.Run(() => WaitForTakeOver(process, ready));
            }
        }
        catch
        {
            tookOver = false;
        }

        if (tookOver)
        {
            ExitApplication();
            return true;
        }

        // Resume as before: the new process is gone, so the mutex and the port are free again.
        AcquireInstanceMutex(waitForPrevious: true);
        await StartServerAsync(retry: true);
        if (wasVisible) ShowMainWindow();
        return false;
    }

    private static bool WaitForTakeOver(Process process, EventWaitHandle ready)
    {
        using var exited = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(process.Handle, ownsHandle: false) };
        int signal = WaitHandle.WaitAny(new WaitHandle[] { ready, exited }, TimeSpan.FromSeconds(20));
        if (signal == 0) return true;
        if (signal == 1) return false;
        // Timed out: an older version (rollback to 0.1.0) does not signal; alive means it started.
        return !process.HasExited;
    }
}
