using System.Windows;
using NativeNCA.App.Views;
using NativeNCA.Core.Models;
using NativeNCA.Core.Security;
using NativeNCA.Core.Services;
using NativeNCA.Core.ViewModels;

namespace NativeNCA.App.Services;

public class WpfUserPromptService : IUserPromptService
{
    private readonly SecureVaultService _vault;
    private readonly ConsentCache _consentCache;

    public WpfUserPromptService(SecureVaultService vault, ConsentCache consentCache)
    {
        _vault = vault;
        _consentCache = consentCache;
    }

    /// <summary>Raised for notifications; the tray icon shows them as balloon tips.</summary>
    public event Action<string, string>? NotificationRequested;

    public Task<SigningPromptResult> PromptForSigningAsync(SigningPromptRequest request, CancellationToken cancellationToken = default)
    {
        if (Application.Current == null)
            return Task.FromResult(SigningPromptResult.Cancelled);

        return Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            var vm = new SignOverlayViewModel(_vault, _consentCache, request);
            var window = new SignOverlayWindow(vm);

            // The website closed the connection (tab closed, page reloaded): withdraw the prompt.
            await using var registration = cancellationToken.Register(() =>
                Application.Current.Dispatcher.BeginInvoke(vm.Cancel));

            window.Show();
            var result = await vm.Task;
            window.Close();
            return result;
        }).Task.Unwrap();
    }

    public Task ShowNotificationAsync(string title, string message)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (NotificationRequested != null)
                NotificationRequested(title, message);
            else
                MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        });
        return Task.CompletedTask;
    }
}
