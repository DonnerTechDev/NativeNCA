using NativeNCA.Core.ViewModels;
using Wpf.Ui.Controls;

namespace NativeNCA.App.Views.Dialogs;

public partial class ChangePasswordDialog : ContentDialog
{
    private readonly SettingsViewModel _viewModel;
    private readonly KeyItemViewModel _key;

    public ChangePasswordDialog(ContentDialogHost host, SettingsViewModel viewModel, KeyItemViewModel key)
        : base(host)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _key = key;

        KeyNameText.Text = $"Ключ «{key.Name}» будет перешифрован новым паролем. Запомните его — восстановить пароль невозможно.";
        VaultHint.Visibility = key.IsPasswordSaved ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        Loaded += (_, _) => CurrentPasswordInput.Focus();
    }

    protected override async void OnButtonClick(ContentDialogButton button)
    {
        if (button != ContentDialogButton.Primary)
        {
            base.OnButtonClick(button);
            return;
        }

        string current = CurrentPasswordInput.Password;
        string next = NewPasswordInput.Password;

        if (string.IsNullOrEmpty(current) || string.IsNullOrEmpty(next))
        {
            ShowError("Заполните текущий и новый пароль");
            return;
        }

        if (next != ConfirmPasswordInput.Password)
        {
            ShowError("Новые пароли не совпадают");
            ConfirmPasswordInput.Focus();
            return;
        }

        if (next == current)
        {
            ShowError("Новый пароль совпадает с текущим");
            return;
        }

        IsPrimaryButtonEnabled = false;
        try
        {
            await _viewModel.ChangeKeyPasswordAsync(_key.Id, current, next);
        }
        catch (Exception ex)
        {
            ShowError(AddKeyDialog.FriendlyError(ex));
            CurrentPasswordInput.Focus();
            return;
        }
        finally
        {
            IsPrimaryButtonEnabled = true;
        }

        base.OnButtonClick(button);
    }

    private void ShowError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }
}
