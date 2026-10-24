using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using NativeNCA.App.Services;
using NativeNCA.App.Views.Dialogs;
using NativeNCA.Core.Security;
using NativeNCA.Core.Updates;
using NativeNCA.Core.ViewModels;
using Wpf.Ui;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace NativeNCA.App.Views;

public partial class MainWindow : FluentWindow
{
    private readonly SettingsViewModel _viewModel;
    private readonly UpdateViewModel _updates;
    private readonly SnackbarService _snackbar = new();
    private readonly TrayNotification _trayNotification;
    private bool _isSettingsOpen;
    private FrameworkElement? _currentView;

    public MainWindow(SettingsViewModel viewModel, UpdateViewModel updates)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _updates = updates;
        DataContext = viewModel;
        UpdateBanner.DataContext = updates;
        RestartBanner.DataContext = updates;
        UpdatesSection.DataContext = updates;
        UninstallCard.DataContext = updates;

        SystemThemeWatcher.Watch(this);
        _snackbar.SetSnackbarPresenter(SnackbarPresenter);
        SourceInitialized += (_, _) => _viewModel.OwnerWindowHandle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        TrayIcon.Icon = LoadTrayIcon();
        _trayNotification = new TrayNotification(TrayIcon);
        _trayNotification.Clicked += (_, _) => Dispatcher.BeginInvoke(BringToFront);

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _updates.PropertyChanged += OnUpdatesPropertyChanged;
        _updates.NotificationRequested += (_, release) => ShowUpdateNotification(release);
        _updates.InstallRequired += async (_, _) => await OfferInstallAsync();
        Loaded += OnLoaded;
        UpdateBannerHost();
    }

    // ===================== Lifetime & tray =====================

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    /// <summary>
    /// The notification area shows 16 px icons scaled by the system DPI (24 px at 150%).
    /// WPF-UI's tray converter always takes the first frame of a multi-frame icon (16 px) and treats the
    /// pixels as premultiplied, so hand it a plain bitmap of exactly the right size in Pbgra32;
    /// otherwise Windows stretches the 16 px frame and the icon looks blurry.
    /// </summary>
    private static ImageSource LoadTrayIcon()
    {
        int wanted = (int)Math.Round(16 * GetDpiForSystem() / 96.0);

        var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
            new Uri("pack://application:,,,/NativeNCA;component/Assets/app.ico"),
            System.Windows.Media.Imaging.BitmapCreateOptions.None,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);

        // Smallest frame that is at least the wanted size (every common DPI has an exact frame).
        System.Windows.Media.Imaging.BitmapSource frame = decoder.Frames
            .Where(f => f.PixelWidth >= wanted)
            .OrderBy(f => f.PixelWidth)
            .FirstOrDefault() ?? decoder.Frames.OrderByDescending(f => f.PixelWidth).First();

        if (frame.PixelWidth != wanted)
        {
            double scale = (double)wanted / frame.PixelWidth;
            frame = new System.Windows.Media.Imaging.TransformedBitmap(frame, new ScaleTransform(scale, scale));
        }

        var bitmap = new System.Windows.Media.Imaging.FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
        bitmap.Freeze();
        return bitmap;
    }

    private bool _isExiting;
    private bool _startHidden;

    /// <summary>The user chose "Выход" in the tray menu.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>Install this (not installed) copy and hand over to the installed one.</summary>
    public event EventHandler? InstallRequested;

    /// <summary>Settings → "Удалить NativeNCA".</summary>
    public event EventHandler? UninstallRequested;

    /// <summary>
    /// Starts in the tray only (autostart). The tray icon registers when the window is loaded,
    /// so the window is shown once off-screen without activation and hidden right away.
    /// </summary>
    public void InitializeHidden()
    {
        _startHidden = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = Top = -32000;
        ShowActivated = false;
        ShowInTaskbar = false;
        Show();
    }

    public void BringToFront()
    {
        if (_startHidden)
        {
            // First real appearance after a background start: restore normal placement.
            _startHidden = false;
            ShowInTaskbar = true;
            ShowActivated = true;
            Left = (SystemParameters.WorkArea.Width - Width) / 2 + SystemParameters.WorkArea.Left;
            Top = (SystemParameters.WorkArea.Height - Height) / 2 + SystemParameters.WorkArea.Top;
        }

        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;  // reliably come to front when activated from the tray
        Topmost = false;
        Focus();

        // Before Loaded the dialog host is not ready; OnLoaded makes the offer then.
        if (IsLoaded) _ = OfferTlsSetupAsync();
    }

    public void CloseForExit()
    {
        _isExiting = true;
        TrayIcon.Unregister();
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing the window keeps NativeNCA running in the tray: websites still need the server.
        if (!_isExiting)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    private void OnTrayLeftClick(object sender, RoutedEventArgs e) => BringToFront();

    private void OnTrayOpenClick(object sender, RoutedEventArgs e) => BringToFront();

    private void OnTraySettingsClick(object sender, RoutedEventArgs e)
    {
        BringToFront();
        _isSettingsOpen = true;
        UpdateView(animate: true);
    }

    private void OnTrayExitClick(object sender, RoutedEventArgs e) => ExitRequested?.Invoke(this, EventArgs.Empty);

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_startHidden) Hide();

        UpdateView(animate: false);
        HelloUnavailableBar.IsOpen = !await WindowsHelloService.IsAvailableAsync();

        if (!_startHidden) await OfferTlsSetupAsync();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.StatusMessage) && !string.IsNullOrEmpty(_viewModel.StatusMessage))
        {
            ShowToast(_viewModel.StatusMessage);
            // Reset so that the same message shows again next time.
            _viewModel.StatusMessage = null;
        }
        if (e.PropertyName == nameof(SettingsViewModel.ServerError)) UpdateBannerHost();
    }

    private void OnUpdatesPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(UpdateViewModel.IsUpdateBannerVisible):
            case nameof(UpdateViewModel.RestartBannerText):
                UpdateBannerHost();
                break;
            case nameof(UpdateViewModel.RollbackError) when _updates.RollbackError != null:
                ShowError(_updates.RollbackError);
                break;
        }
    }

    /// <summary>The banner stack takes no space when every banner is hidden.</summary>
    private void UpdateBannerHost()
    {
        // Bindings update after the property change notification, so look once they have run.
        Dispatcher.BeginInvoke(() =>
        {
            bool any = BannerHost.Children.OfType<UIElement>().Any(b => b.Visibility == Visibility.Visible);
            BannerHost.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        }, System.Windows.Threading.DispatcherPriority.DataBind);
    }

    public void ShowError(string message) => ShowToast(message.StartsWith("Ошибка", StringComparison.Ordinal) ? message : "Ошибка: " + message, isError: true);

    // ===================== Updates =====================

    private void ShowUpdateNotification(ReleaseInfo release)
    {
        _trayNotification.Show($"Доступна NativeNCA {release.Version}",
            $"У вас версия {_updates.CurrentVersion}. Нажмите, чтобы открыть NativeNCA и обновить");
    }

    private void OnOpenSettingsFromBannerClick(object sender, RoutedEventArgs e)
    {
        _isSettingsOpen = true;
        UpdateView(animate: true);
    }

    private void OnInstallClick(object sender, RoutedEventArgs e) => InstallRequested?.Invoke(this, EventArgs.Empty);

    private void OnUninstallClick(object sender, RoutedEventArgs e) => UninstallRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>"Обновить" in a copy that is not installed: only the installed copy updates itself.</summary>
    private async Task OfferInstallAsync()
    {
        var dialog = new ContentDialog(DialogHost)
        {
            Title = "Установить NativeNCA?",
            Content = new TextBlock
            {
                Text = "Эта копия запущена без установки, поэтому обновить её автоматически нельзя. Установите NativeNCA — " +
                       "он появится в меню «Пуск», а обновление можно будет поставить сразу после запуска.\n\n" +
                       "Или скачайте новую версию вручную со страницы релиза.",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 440,
                Style = (Style)FindResource("SecondaryText"),
                FontSize = 13
            },
            PrimaryButtonText = "Установить",
            SecondaryButtonText = "Страница релиза",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Primary
        };

        switch (await dialog.ShowAsync())
        {
            case ContentDialogResult.Primary:
                InstallRequested?.Invoke(this, EventArgs.Empty);
                break;
            case ContentDialogResult.Secondary:
                OpenUrl(_updates.ReleaseNotesUrl);
                break;
        }
    }

    private async void OnRollbackClick(object sender, RoutedEventArgs e)
    {
        if (_updates.PreviousVersion is not { } previous) return;

        var dialog = new ContentDialog(DialogHost)
        {
            Title = $"Вернуть версию {previous}?",
            Content = new TextBlock
            {
                Text = $"NativeNCA перезапустится в версии {previous}. Ключи и параметры сохранятся. " +
                       $"Версия {_updates.CurrentVersion} больше не будет предлагаться автоматически — " +
                       "её можно будет снова установить кнопкой «Проверить обновления».",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 440,
                Style = (Style)FindResource("SecondaryText"),
                FontSize = 13
            },
            PrimaryButtonText = "Вернуть",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await _updates.RollBackAsync();
    }

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser */ }
    }

    // ===================== Navigation between key details / settings =====================

    private void OnKeySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel.SelectedKey != null) _isSettingsOpen = false;
        UpdateView(animate: true);
    }

    private void OnKeysListClicked(object sender, MouseButtonEventArgs e)
    {
        // Clicking the already selected key while settings are open should bring its details back.
        if (_isSettingsOpen && _viewModel.SelectedKey != null)
        {
            _isSettingsOpen = false;
            UpdateView(animate: true);
        }
    }

    private void OnSettingsNavClick(object sender, RoutedEventArgs e)
    {
        _isSettingsOpen = true;
        UpdateView(animate: true);
    }

    private void UpdateView(bool animate)
    {
        FrameworkElement target = _isSettingsOpen
            ? SettingsView
            : _viewModel.SelectedKey != null ? KeyDetailsView : EmptyView;

        SettingsNavButton.IsChecked = _isSettingsOpen;
        KeysList.SelectedItem = _isSettingsOpen ? null : _viewModel.SelectedKey;

        SettingsView.Visibility = target == SettingsView ? Visibility.Visible : Visibility.Collapsed;
        KeyDetailsView.Visibility = target == KeyDetailsView ? Visibility.Visible : Visibility.Collapsed;
        EmptyView.Visibility = target == EmptyView ? Visibility.Visible : Visibility.Collapsed;

        if (target is ScrollViewer scroll && target != _currentView) scroll.ScrollToTop();

        if (animate)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var duration = TimeSpan.FromMilliseconds(220);
            ContentHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
            ((TranslateTransform)ContentHost.RenderTransform).BeginAnimation(
                TranslateTransform.YProperty, new DoubleAnimation(10, 0, duration) { EasingFunction = ease });
        }

        _currentView = target;
    }

    // ===================== Key actions =====================

    private async void OnAddKeyClick(object sender, RoutedEventArgs e)
    {
        await new AddKeyDialog(DialogHost, _viewModel).ShowAsync();
    }

    private async void OnAvatarClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedKey is not { } key) return;

        var dialog = new AvatarCropDialog(DialogHost, key.AvatarPath);
        await dialog.ShowAsync();

        try
        {
            if (dialog.ResultPng is { } png)
                _viewModel.SetAvatar(key.Id, png);
            else if (dialog.RemoveRequested)
                _viewModel.RemoveAvatar(key.Id);
        }
        catch (Exception ex)
        {
            ShowToast("Ошибка: " + ex.Message);
        }
    }

    private async void OnChangePasswordClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedKey is not { } key) return;
        await new ChangePasswordDialog(DialogHost, _viewModel, key).ShowAsync();
    }

    private async void OnRenameKeyClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedKey is not { } key) return;

        var input = new Wpf.Ui.Controls.TextBox
        {
            Text = key.Name,
            PlaceholderText = "Например: «Личный eGov»",
            MaxLength = 64,
            ClearButtonEnabled = true
        };

        var dialog = new ContentDialog(DialogHost)
        {
            Title = "Переименовать ключ",
            Content = new StackPanel
            {
                Width = 400,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Название видно только вам — в списке ключей и в окне подтверждения подписи.",
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 0, 0, 16),
                        Style = (Style)FindResource("SecondaryText"),
                        FontSize = 13
                    },
                    input
                }
            },
            PrimaryButtonText = "Сохранить",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            _viewModel.RenameKey(key.Id, input.Text);
    }

    private async void OnDeleteKeyClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedKey is not { } key) return;

        var dialog = new ContentDialog(DialogHost)
        {
            Title = $"Удалить «{key.Name}»?",
            Content = new TextBlock
            {
                Text = "Зашифрованная копия ключа и сохранённый пароль будут удалены из хранилища NativeNCA. " +
                       "Исходный файл .p12 на диске останется — при необходимости ключ можно будет добавить заново.",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420,
                Style = (Style)FindResource("SecondaryText"),
                FontSize = 13
            },
            PrimaryButtonText = "Удалить",
            PrimaryButtonAppearance = ControlAppearance.Danger,
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            _viewModel.DeleteSelectedKeyCommand.Execute(null);
    }

    private async void OnSavePasswordToggleClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedKey is not { } key) return;

        try
        {
            if (SavePasswordToggle.IsChecked == true)
                await PromptAndSavePasswordAsync(key);
            else
                await _viewModel.SetPasswordSavedAsync(key.Id, false);
        }
        catch (Exception ex)
        {
            ShowToast("Ошибка: " + AddKeyDialog.FriendlyError(ex));
        }
        finally
        {
            // Snap the switch back to the real state if the user cancelled.
            BindingOperations.GetBindingExpression(SavePasswordToggle, ToggleButtonIsCheckedProperty)?.UpdateTarget();
        }
    }

    private static readonly DependencyProperty ToggleButtonIsCheckedProperty =
        System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty;

    private async Task PromptAndSavePasswordAsync(KeyItemViewModel key)
    {
        var password = new Wpf.Ui.Controls.PasswordBox
        {
            PlaceholderText = "Пароль от ключа",
            Icon = new SymbolIcon(SymbolRegular.Password24)
        };
        var error = new InfoBar
        {
            Severity = InfoBarSeverity.Error,
            IsClosable = false,
            Title = "Неверный пароль",
            Message = "Проверьте раскладку клавиатуры и попробуйте ещё раз",
            Margin = new Thickness(0, 12, 0, 0)
        };

        var dialog = new ContentDialog(DialogHost)
        {
            Title = "Сохранить пароль в Vault",
            Content = new StackPanel
            {
                Width = 400,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"Введите пароль от ключа «{key.Name}». Он будет проверен и сохранён в зашифрованном виде.",
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 0, 0, 16),
                        Style = (Style)FindResource("SecondaryText"),
                        FontSize = 13
                    },
                    password,
                    error
                }
            },
            PrimaryButtonText = "Сохранить",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.Loaded += (_, _) => password.Focus();

        // Re-show the dialog until the password is accepted or the user cancels.
        while (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            try
            {
                await _viewModel.SetPasswordSavedAsync(key.Id, true, password.Password);
                return;
            }
            catch (Exception ex)
            {
                error.Title = ex is OperationCanceledException ? "Windows Hello" : "Неверный пароль";
                error.Message = ex is InvalidOperationException
                    ? "Проверьте раскладку клавиатуры и попробуйте ещё раз"
                    : AddKeyDialog.FriendlyError(ex);
                error.IsOpen = true;
            }
        }
    }

    private async void OnHelloVaultToggleClick(object sender, RoutedEventArgs e)
    {
        bool enable = HelloVaultToggle.IsChecked == true;
        try
        {
            if (enable)
            {
                var confirm = new ContentDialog(DialogHost)
                {
                    Title = "Защитить хранилище Windows Hello?",
                    Content = new TextBlock
                    {
                        Text = "Ключи и сохранённые пароли будут дополнительно зашифрованы ключом Windows Hello, " +
                               "который хранится в TPM и не покидает этот компьютер. Для подписи и изменений " +
                               "в хранилище понадобится лицо, отпечаток или PIN-код.\n\n" +
                               "Важно: если сбросить PIN-код или переустановить Windows Hello, расшифровать хранилище " +
                               "будет нельзя. Сохраните исходные файлы .p12 и пароли от них — тогда ключи можно добавить заново.",
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 440,
                        Style = (Style)FindResource("SecondaryText"),
                        FontSize = 13
                    },
                    PrimaryButtonText = "Включить защиту",
                    CloseButtonText = "Отмена",
                    DefaultButton = ContentDialogButton.Primary
                };

                if (await confirm.ShowAsync() == ContentDialogResult.Primary)
                    await _viewModel.EnableHelloProtectionAsync();
            }
            else
            {
                await _viewModel.DisableHelloProtectionAsync();
            }
        }
        catch (Exception ex)
        {
            ShowToast("Ошибка: " + AddKeyDialog.FriendlyError(ex));
        }
        finally
        {
            BindingOperations.GetBindingExpression(HelloVaultToggle, ToggleButtonIsCheckedProperty)?.UpdateTarget();
        }
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string text } && !string.IsNullOrWhiteSpace(text))
        {
            Clipboard.SetText(text);
            ShowToast($"Скопировано: {text}");
        }
    }

    // ===================== Browser certificate =====================

    private bool _tlsSetupOffered;

    /// <summary>First appearance: explain and offer the own certificate once, until it is set up or declined.</summary>
    private async Task OfferTlsSetupAsync()
    {
        if (_tlsSetupOffered || !_viewModel.ShouldOfferTlsSetup) return;
        _tlsSetupOffered = true;
        await SetUpTlsCertificateAsync(declineOnClose: true);
    }

    private async void OnInstallTlsCertificateClick(object sender, RoutedEventArgs e) =>
        await SetUpTlsCertificateAsync(declineOnClose: false);

    private async Task SetUpTlsCertificateAsync(bool declineOnClose)
    {
        if (await new TlsSetupDialog(DialogHost).ShowAsync() != ContentDialogResult.Primary)
        {
            if (declineOnClose) _viewModel.DeclineTlsSetup();
            return;
        }

        try
        {
            await _viewModel.InstallTlsCertificateAsync();
        }
        catch (OperationCanceledException)
        {
            ShowToast("Сертификат не установлен: в окне Windows выбран ответ «Нет»", isError: true);
        }
        catch (Exception ex)
        {
            ShowToast("Ошибка: " + ex.Message);
        }
    }

    private async void OnRemoveTlsCertificateClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog(DialogHost)
        {
            Title = "Удалить сертификат NativeNCA?",
            Content = new TextBlock
            {
                Text = "Корневой сертификат будет удалён из Windows, а сертификат сервера — с диска. " +
                       "Windows попросит подтвердить удаление.\n\n" +
                       "Без него браузеры смогут подключиться к NativeNCA, только если на компьютере установлен NCALayer. " +
                       "Собственный сертификат можно снова установить в любой момент.",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 440,
                Style = (Style)FindResource("SecondaryText"),
                FontSize = 13
            },
            PrimaryButtonText = "Удалить",
            PrimaryButtonAppearance = ControlAppearance.Danger,
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            await _viewModel.RemoveTlsCertificateAsync();
        }
        catch (OperationCanceledException)
        {
            ShowToast("Сертификат не удалён: в окне Windows выбран ответ «Нет»", isError: true);
        }
        catch (Exception ex)
        {
            ShowToast("Ошибка: " + ex.Message);
        }
    }

    // ===================== Original NCALayer =====================

    private async void OnCloseNcaLayerClick(object sender, RoutedEventArgs e)
    {
        var alsoAutostart = new CheckBox
        {
            Content = "Также отключить автозапуск NCALayer",
            IsChecked = true,
            Margin = new Thickness(0, 16, 0, 0),
            Visibility = _viewModel.IsNcaLayerAutostartEnabled ? Visibility.Visible : Visibility.Collapsed
        };

        var dialog = new ContentDialog(DialogHost)
        {
            Title = "Закрыть NCALayer?",
            Content = new StackPanel
            {
                Width = 420,
                Children =
                {
                    new TextBlock
                    {
                        Text = "NCALayer будет завершён, и NativeNCA начнёт принимать запросы сайтов на порту 13579. " +
                               "Если NCALayer сейчас что-то подписывает, операция прервётся.",
                        TextWrapping = TextWrapping.Wrap,
                        Style = (Style)FindResource("SecondaryText"),
                        FontSize = 13
                    },
                    alsoAutostart
                }
            },
            PrimaryButtonText = "Закрыть NCALayer",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        if (alsoAutostart.Visibility == Visibility.Visible && alsoAutostart.IsChecked == true)
            DisableNcaLayerAutostart(quiet: true);

        if (await _viewModel.CloseNcaLayerAsync())
            ShowToast("NCALayer закрыт — NativeNCA принимает запросы сайтов");
        else
            ShowToast("Ошибка: NCALayer не закрылся. Закройте его через значок в трее (Выход) и нажмите «Повторить»");
    }

    private async void OnDisableNcaLayerAutostartClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog(DialogHost)
        {
            Title = "Отключить автозапуск NCALayer?",
            Content = new TextBlock
            {
                Text = "NCALayer перестанет запускаться при входе в Windows и занимать порт 13579. Сама программа " +
                       "не удаляется: её можно запустить вручную или снова включить в «Параметры Windows → Приложения → Автозагрузка».",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 440,
                Style = (Style)FindResource("SecondaryText"),
                FontSize = 13
            },
            PrimaryButtonText = "Отключить",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            DisableNcaLayerAutostart(quiet: false);
    }

    private void DisableNcaLayerAutostart(bool quiet)
    {
        if (_viewModel.DisableNcaLayerAutostart())
        {
            if (!quiet) ShowToast("Автозапуск NCALayer отключён");
            return;
        }

        // Installed for all users: StartupApproved under HKLM needs administrator rights, Windows Settings has them.
        ShowToast("Автозапуск NCALayer для всех пользователей отключается в параметрах Windows — отключите NCALayer в открывшемся списке", isError: true);
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:startupapps") { UseShellExecute = true }); }
        catch { /* Settings unavailable: the toast still explains where to go */ }
    }

    private void ShowToast(string message) => ShowToast(message, message.StartsWith("Ошибка", StringComparison.OrdinalIgnoreCase));

    private void ShowToast(string message, bool isError)
    {
        _snackbar.Show(
            isError ? "Ошибка" : "NativeNCA",
            message,
            isError ? ControlAppearance.Danger : ControlAppearance.Secondary,
            new SymbolIcon(isError ? SymbolRegular.ErrorCircle24 : SymbolRegular.CheckmarkCircle24),
            TimeSpan.FromSeconds(3));
    }
}
