using System.Windows;
using NativeNCA.App.Services;
using NativeNCA.App.Views;
using NativeNCA.Core.Security;
using NativeNCA.Core.Server;
using NativeNCA.Core.ViewModels;
using Wpf.Ui.Appearance;

namespace NativeNCA.App;

public partial class App : Application
{
    private const string InstanceMutexName = @"Local\NativeNCA.SingleInstance";
    private const string ShowEventName = @"Local\NativeNCA.ShowMainWindow";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showEvent;
    private NativeNcaServer? _server;
    private MainWindow? _mainWindow;

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        // Only one NativeNCA may own port 13579: a second launch just brings the first one forward.
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { /* first instance is shutting down */ }
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        new Thread(() =>
        {
            while (_showEvent.WaitOne())
                Dispatcher.BeginInvoke(ShowMainWindow);
        }) { IsBackground = true, Name = "NativeNCA.ShowSignal" }.Start();

        // Follow the Windows light/dark theme and accent colour instead of a hard-coded theme.
        ApplicationThemeManager.ApplySystemTheme(true);

        var vault = new SecureVaultService();
        var promptService = new WpfUserPromptService(vault, new ConsentCache());
        _server = new NativeNcaServer(vault, new TlsCertificateService(), promptService);

        var settingsVm = new SettingsViewModel(vault) { RetryServerAction = () => _server.Start() };

        _server.StateChanged += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            settingsVm.IsServerRunning = _server.IsRunning;
            settingsVm.ServerError = _server.StartError;
        });
        // Server problems are shown as a banner in the main window, so bring it up.
        promptService.NotificationRequested += (_, _) => ShowMainWindow();

        _mainWindow = new MainWindow(settingsVm);
        _mainWindow.ExitRequested += (_, _) => ExitApplication();

        bool startInBackground = e.Args.Contains(AutoStartService.BackgroundArgument, StringComparer.OrdinalIgnoreCase);
        if (startInBackground)
            _mainWindow.InitializeHidden();
        else
            _mainWindow.Show();

        _server.Start();

        // Keep the Run entry pointing at the current exe (e.g. after the app was moved or updated).
        AutoStartService.Apply(vault.GetSettings().AutoStartWithWindows);
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
        if (_instanceMutex != null)
        {
            _instanceMutex.ReleaseMutex();
            _instanceMutex.Dispose();
        }
        base.OnExit(e);
    }
}
