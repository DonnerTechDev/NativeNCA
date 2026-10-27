using System.Windows;
using Microsoft.Win32;
using NativeNCA.Core.ViewModels;
using Wpf.Ui.Controls;

namespace NativeNCA.App.Views;

public partial class CsrOverlayWindow : FluentWindow
{
    public CsrOverlayViewModel ViewModel { get; }

    public CsrOverlayWindow(CsrOverlayViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this);

        Loaded += (_, _) =>
        {
            Activate();
            Focus();
        };

        Closing += (_, _) =>
        {
            if (!ViewModel.Task.IsCompleted)
                ViewModel.CancelCommand.Execute(null);
        };
    }

    private void OnBrowseFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = Title,
            InitialDirectory = ViewModel.BackupFolder
        };

        if (dialog.ShowDialog(this) == true)
        {
            ViewModel.BackupFolder = dialog.FolderName;
        }
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        ViewModel.Password = PasswordInput.Password;
    }
}
