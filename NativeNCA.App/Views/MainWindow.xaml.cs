using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using NativeNCA.App.Views.Dialogs;
using NativeNCA.Core.Security;
using NativeNCA.Core.ViewModels;
using Wpf.Ui;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace NativeNCA.App.Views;

public partial class MainWindow : FluentWindow
{
    private readonly SettingsViewModel _viewModel;
    private readonly SnackbarService _snackbar = new();
    private bool _isSettingsOpen;
    private FrameworkElement? _currentView;

    public MainWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        SystemThemeWatcher.Watch(this);
        _snackbar.SetSnackbarPresenter(SnackbarPresenter);
        SourceInitialized += (_, _) => _viewModel.OwnerWindowHandle = new System.Windows.Interop.WindowInteropHelper(this).Handle;

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += OnLoaded;
    }

    // ===================== Lifetime & tray =====================

    private bool _isExiting;
    private bool _startHidden;

    /// <summary>The user chose "Выход" in the tray menu.</summary>
    public event EventHandler? ExitRequested;

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
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.StatusMessage) && !string.IsNullOrEmpty(_viewModel.StatusMessage))
        {
            ShowToast(_viewModel.StatusMessage);
            // Reset so that the same message shows again next time.
            _viewModel.StatusMessage = null;
        }
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

    private void ShowToast(string message)
    {
        bool isError = message.StartsWith("Ошибка", StringComparison.OrdinalIgnoreCase);
        _snackbar.Show(
            isError ? "Ошибка" : "NativeNCA",
            message,
            isError ? ControlAppearance.Danger : ControlAppearance.Secondary,
            new SymbolIcon(isError ? SymbolRegular.ErrorCircle24 : SymbolRegular.CheckmarkCircle24),
            TimeSpan.FromSeconds(3));
    }
}
