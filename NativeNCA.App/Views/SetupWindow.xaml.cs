using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NativeNCA.Core.Updates;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace NativeNCA.App.Views;

public enum SetupResult
{
    /// <summary>The window was closed without a choice.</summary>
    None,
    Installed,
    OpenedInstalled,
    RunWithoutInstalling,
    Uninstalled
}

/// <summary>
/// "Установить NativeNCA" when the exe runs from outside the install folder, and the confirmation for
/// <c>--uninstall</c>. The actual work is passed in by <see cref="App"/>.
/// </summary>
public partial class SetupWindow : FluentWindow
{
    private Func<Task>? _primaryAction;
    private Action? _secondaryAction;
    private bool _isBusy;
    private bool _isDone;

    public SetupResult Result { get; private set; }

    private SetupWindow()
    {
        InitializeComponent();
        SystemThemeWatcher.Watch(this);
        AppIconImage.Source = LoadAppIcon(56);
    }

    /// <param name="install">Closes a running NativeNCA, installs this exe and starts the installed copy.</param>
    /// <param name="openInstalled">Brings the installed copy up (shows it or starts it).</param>
    public static SetupWindow ForInstall(AppVersion version, AppVersion? installedVersion, Func<Task> install, Action openInstalled)
    {
        var window = new SetupWindow();
        window.SecondaryLink.Content = "Запустить без установки";
        window._secondaryAction = () => window.Finish(SetupResult.RunWithoutInstalling);

        if (installedVersion != null && installedVersion >= version)
        {
            window.HeaderTitle.Text = "NativeNCA уже установлен";
            window.HeaderSubtitle.Text = $"На этом компьютере установлена версия {installedVersion}";
            window.ShowInfo(installedVersion.Equals(version)
                ? "Эта же версия уже установлена. Откройте её — она обновляется сама."
                : $"Установлена более новая версия, чем эта копия ({version}). Откройте установленную, а эту копию можно удалить.");
            window.PrimaryLabel.Text = "Открыть установленную";
            window._primaryAction = () =>
            {
                openInstalled();
                window.Finish(SetupResult.OpenedInstalled);
                return Task.CompletedTask;
            };
        }
        else
        {
            window.HeaderTitle.Text = "Установить NativeNCA";
            window.HeaderSubtitle.Text = $"Версия {version} · подпись ЭЦП НУЦ РК на сайтах без NCALayer";
            if (installedVersion != null)
                window.ShowInfo($"Установлена версия {installedVersion}. Она будет заменена версией {version}; " +
                                "вернуть её можно будет в параметрах.");
            window.PrimaryLabel.Text = "Установить";
            window._primaryAction = async () =>
            {
                await install();
                window.Finish(SetupResult.Installed);
            };
        }

        return window;
    }

    /// <param name="uninstall">Removes NativeNCA (and the data when asked); returns a warning to show, or null.</param>
    public static SetupWindow ForUninstall(AppVersion version, Func<bool, Task<string?>> uninstall)
    {
        var window = new SetupWindow { Title = "Удаление NativeNCA" };
        window.HeaderTitle.Text = "Удалить NativeNCA?";
        window.HeaderSubtitle.Text = $"Версия {version}";
        window.InstallContent.Visibility = Visibility.Collapsed;
        window.UninstallContent.Visibility = Visibility.Visible;
        window.SecondaryLink.Visibility = Visibility.Collapsed;
        window.CloseButton.Visibility = Visibility.Visible;
        window.PrimaryLabel.Text = "Удалить";
        window.PrimaryButton.Appearance = ControlAppearance.Danger;
        window.PrimaryButton.IsDefault = false;
        window.CloseButton.IsDefault = true;

        window._primaryAction = async () =>
        {
            bool removeData = window.RemoveDataCheckBox.IsChecked == true;
            string? warning = await uninstall(removeData);
            window.ShowUninstalled(removeData, warning);
        };
        return window;
    }

    private void ShowUninstalled(bool removedData, string? warning)
    {
        _isDone = true;
        Result = SetupResult.Uninstalled;
        HeaderTitle.Text = "NativeNCA удалён";
        HeaderSubtitle.Text = "Программа удалена с этого компьютера";
        UninstallContent.Visibility = Visibility.Collapsed;
        CloseButton.Visibility = Visibility.Collapsed;
        PrimaryButton.Appearance = ControlAppearance.Primary;
        PrimaryButton.IsDefault = true;
        PrimaryLabel.Text = "Закрыть";
        _primaryAction = () =>
        {
            Close();
            return Task.CompletedTask;
        };

        if (warning != null)
            ShowError(warning);
        else if (!removedData)
            ShowInfo("Ключи и параметры остались в вашем профиле Windows и появятся снова, если установить NativeNCA ещё раз.");
        else
            InfoNote.Visibility = Visibility.Collapsed;
    }

    private void ShowInfo(string text)
    {
        InfoText.Text = text;
        InfoNote.Visibility = Visibility.Visible;
    }

    private void ShowError(string? text)
    {
        ErrorText.Text = text;
        ErrorNote.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Finish(SetupResult result)
    {
        _isDone = true;
        Result = result;
        Close();
    }

    private async void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        if (_primaryAction == null || _isBusy) return;
        SetBusy(true);
        ShowError(null);
        try
        {
            await _primaryAction();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnSecondaryClick(object sender, RoutedEventArgs e)
    {
        if (!_isBusy) _secondaryAction?.Invoke();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (!_isBusy) Close();
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        BusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        PrimaryButton.IsEnabled = !busy;
        SecondaryLink.IsEnabled = !busy;
        CloseButton.IsEnabled = !busy;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Never leave a half-done install or uninstall behind.
        if (_isBusy && !_isDone) e.Cancel = true;
        base.OnClosing(e);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    /// <summary>The app icon frame that is at least <paramref name="size"/> device pixels, so it stays crisp.</summary>
    internal static ImageSource LoadAppIcon(double size)
    {
        int wanted = (int)Math.Ceiling(size * Math.Max(1, GetDpiForSystem() / 96.0));
        var decoder = BitmapDecoder.Create(new Uri("pack://application:,,,/NativeNCA;component/Assets/app.ico"),
            BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames.Where(f => f.PixelWidth >= wanted).OrderBy(f => f.PixelWidth).FirstOrDefault()
                             ?? decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
        frame.Freeze();
        return frame;
    }
}
