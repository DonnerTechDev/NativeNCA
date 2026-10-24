using Windows.Security.Credentials.UI;

namespace NativeNCA.Core.Security;

public enum HelloResult
{
    Verified,
    Canceled,
    /// <summary>No PIN/biometrics configured, or the device is busy/disabled by policy.</summary>
    NotAvailable,
    Failed
}

public static class WindowsHelloService
{
    public static async Task<bool> IsAvailableAsync()
    {
        try
        {
            return await UserConsentVerifier.CheckAvailabilityAsync() == UserConsentVerifierAvailability.Available;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Shows the Windows Hello prompt. When <paramref name="ownerWindow"/> is given the prompt is
    /// parented to that window, so it appears on top of it instead of behind other windows.
    /// Never returns <see cref="HelloResult.Verified"/> unless the user actually verified.
    /// </summary>
    public static async Task<HelloResult> RequestConsentAsync(string message, IntPtr ownerWindow = default)
    {
        try
        {
            if (await UserConsentVerifier.CheckAvailabilityAsync() != UserConsentVerifierAvailability.Available)
                return HelloResult.NotAvailable;

            var result = ownerWindow != IntPtr.Zero
                ? await UserConsentVerifierInterop.RequestVerificationForWindowAsync(ownerWindow, message)
                : await UserConsentVerifier.RequestVerificationAsync(message);

            return result switch
            {
                UserConsentVerificationResult.Verified => HelloResult.Verified,
                UserConsentVerificationResult.Canceled => HelloResult.Canceled,
                UserConsentVerificationResult.DeviceNotPresent or
                UserConsentVerificationResult.NotConfiguredForUser or
                UserConsentVerificationResult.DisabledByPolicy or
                UserConsentVerificationResult.DeviceBusy => HelloResult.NotAvailable,
                _ => HelloResult.Failed
            };
        }
        catch
        {
            return HelloResult.Failed;
        }
    }
}
