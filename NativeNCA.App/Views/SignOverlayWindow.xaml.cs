using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using NativeNCA.Core.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace NativeNCA.App.Views;

public partial class SignOverlayWindow : FluentWindow
{
    private readonly SignOverlayViewModel _viewModel;

    public SignOverlayWindow(SignOverlayViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        SystemThemeWatcher.Watch(this);

        SourceInitialized += (_, _) => _viewModel.OwnerWindowHandle = new WindowInteropHelper(this).Handle;
        Loaded += OnLoaded;
        Closing += OnClosing;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PlayEntranceAnimation();

        // The request comes from a background WebSocket thread, so make sure the prompt
        // actually gets foreground focus instead of only flashing in the taskbar.
        Activate();
        Focus();

        if (_viewModel.IsPasswordRequired)
            PasswordInput.Focus();
        else
            ApproveButton.Focus();
    }

    private void PlayEntranceAnimation()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(260);

        RootGrid.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        ((TranslateTransform)RootGrid.RenderTransform).BeginAnimation(
            TranslateTransform.YProperty, new DoubleAnimation(12, 0, duration) { EasingFunction = ease });
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SignOverlayViewModel.IsPasswordRequired) when _viewModel.IsPasswordRequired:
                Dispatcher.BeginInvoke(() => PasswordInput.Focus());
                break;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // Closing via the title bar "X" or Alt+F4 must still complete the pending request,
        // otherwise the website would wait for an answer forever.
        if (!_viewModel.Task.IsCompleted)
            _viewModel.CancelCommand.Execute(null);

        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }
}
