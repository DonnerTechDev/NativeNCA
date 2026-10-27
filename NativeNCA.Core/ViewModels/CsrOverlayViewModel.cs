using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NativeNCA.Core.Localization;
using NativeNCA.Core.Models;

namespace NativeNCA.Core.ViewModels;

public partial class CsrOverlayViewModel : ObservableObject
{
    private readonly CsrPromptRequest _request;
    private readonly TaskCompletionSource<CsrPromptResult> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<CsrPromptResult> Task => _tcs.Task;

    public string OriginUrl => _request.Origin;
    public string OriginDomain => string.IsNullOrEmpty(_request.Origin) ? Strings.Overlay_UnknownOrigin : ExtractDomain(_request.Origin);
    public bool IsSecureOrigin => _request.Origin.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    public string SubjectCommonName => _request.SubjectCommonName ?? string.Empty;
    public string SubjectIin => _request.SubjectIin ?? string.Empty;
    public string SubjectBin => _request.SubjectBin ?? string.Empty;
    public string SubjectOrganization => _request.SubjectOrganization ?? string.Empty;
    public string KeyAlgorithm => _request.KeyAlgorithm;
    public string Email => _request.Email ?? string.Empty;

    public bool HasIin => !string.IsNullOrEmpty(SubjectIin);
    public bool HasBin => !string.IsNullOrEmpty(SubjectBin);
    public bool HasOrganization => !string.IsNullOrEmpty(SubjectOrganization);
    public bool HasEmail => !string.IsNullOrEmpty(Email);

    [ObservableProperty]
    private bool _saveBackupCopy;

    [ObservableProperty]
    private string _backupFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    [ObservableProperty]
    private string _password = string.Empty;

    public CsrOverlayViewModel(CsrPromptRequest request)
    {
        _request = request;
        LocalizationSource.Instance.CultureChanged += OnCultureChanged;
        _tcs.Task.ContinueWith(_ => LocalizationSource.Instance.CultureChanged -= OnCultureChanged, TaskScheduler.Default);
    }

    private void OnCultureChanged(object? sender, EventArgs e) => OnPropertyChanged(string.Empty);

    [RelayCommand]
    public void Approve()
    {
        _tcs.TrySetResult(new CsrPromptResult
        {
            Approved = true,
            Password = string.IsNullOrWhiteSpace(Password) ? null : Password,
            BackupFolder = SaveBackupCopy ? BackupFolder : null
        });
    }

    [RelayCommand]
    public void Cancel()
    {
        _tcs.TrySetResult(CsrPromptResult.Cancelled);
    }

    private static string ExtractDomain(string origin)
    {
        if (Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            return uri.Host;

        string trimmed = origin.Replace("https://", "").Replace("http://", "");
        int slash = trimmed.IndexOf('/');
        return slash >= 0 ? trimmed[..slash] : trimmed;
    }
}
