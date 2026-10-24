using NativeNCA.Core.ViewModels;
using Wpf.Ui.Controls;
using NativeNCA.Core.Localization;

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

        KeyNameText.Text = Loc.Format(Strings.ChangePassword_Intro, key.Name);
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
            ShowError(Strings.ChangePassword_FillBoth);
            return;
        }

        if (next != ConfirmPasswordInput.Password)
        {
            ShowError(Strings.ChangePassword_Mismatch);
            ConfirmPasswordInput.Focus();
            return;
        }

        if (next == current)
        {
            ShowError(Strings.ChangePassword_Same);
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
