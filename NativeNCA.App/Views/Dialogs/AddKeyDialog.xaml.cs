using System.IO;
using System.Windows;
using Microsoft.Win32;
using NativeNCA.Core.ViewModels;
using Wpf.Ui.Controls;
using NativeNCA.Core.Localization;

namespace NativeNCA.App.Views.Dialogs;

public partial class AddKeyDialog : ContentDialog
{
    private static readonly string[] KeyExtensions = [".p12", ".pfx"];

    private readonly SettingsViewModel _viewModel;
    private string? _filePath;

    public AddKeyDialog(ContentDialogHost host, SettingsViewModel viewModel, string? initialFile = null)
        : base(host)
    {
        InitializeComponent();
        _viewModel = viewModel;

        if (initialFile != null) SetFile(initialFile);
    }

    protected override async void OnButtonClick(ContentDialogButton button)
    {
        if (button != ContentDialogButton.Primary)
        {
            base.OnButtonClick(button);
            return;
        }

        if (_filePath == null)
        {
            ShowError(Strings.AddKey_ChooseFile);
            return;
        }

        if (string.IsNullOrEmpty(PasswordInput.Password))
        {
            ShowError(Strings.Overlay_EnterPassword);
            PasswordInput.Focus();
            return;
        }

        IsPrimaryButtonEnabled = false;
        try
        {
            await _viewModel.AddKeyFromFileAsync(
                _filePath,
                PasswordInput.Password,
                string.IsNullOrWhiteSpace(NameInput.Text) ? null : NameInput.Text.Trim(),
                SavePasswordCheck.IsChecked == true);
        }
        catch (Exception ex)
        {
            ShowError(FriendlyError(ex));
            PasswordInput.Focus();
            PasswordInput.SelectAll();
            return;
        }
        finally
        {
            IsPrimaryButtonEnabled = true;
        }

        base.OnButtonClick(button);
    }

    internal static string FriendlyError(Exception ex)
    {
        if (ex is OperationCanceledException)
            return Strings.Hello_Canceled;

        var message = ex.Message;
        if (message.Contains("MAC", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            return Strings.AddKey_WrongPasswordOrDamaged;
        }
        return message;
    }

    private void ShowError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }

    private void SetFile(string path)
    {
        _filePath = path;
        FileNameText.Text = Path.GetFileName(path);
        FilePathText.Text = Path.GetDirectoryName(path);
        FileIcon.Symbol = SymbolRegular.CheckmarkCircle24;
        ErrorBar.IsOpen = false;
        PasswordInput.Focus();
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = Strings.AddKey_FileFilter,
            Title = Strings.AddKey_FileDialogTitle
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            SetFile(dialog.FileName);
    }

    private static string? GetDroppedKeyFile(DragEventArgs e)
    {
        return e.Data.GetData(DataFormats.FileDrop) is string[] files
            ? files.FirstOrDefault(f => KeyExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            : null;
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (GetDroppedKeyFile(e) != null)
            DropZone.BorderBrush = (System.Windows.Media.Brush)FindResource("AccentFillColorDefaultBrush");
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = GetDroppedKeyFile(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e) =>
        DropZone.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");

    private void OnDrop(object sender, DragEventArgs e)
    {
        OnDragLeave(sender, e);
        if (GetDroppedKeyFile(e) is { } file)
            SetFile(file);
    }
}
