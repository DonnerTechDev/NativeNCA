using Microsoft.Win32;

namespace NativeNCA.Core.Security;

/// <summary>Registers NativeNCA in HKCU\...\Run so it starts (minimized to tray) after sign-in.</summary>
public static class AutoStartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "NativeNCA";
    public const string BackgroundArgument = "--background";

    /// <summary>
    /// The exe the Run entry points to: the installed copy when there is one, so that starting a copy from
    /// Downloads does not move autostart away from the installation. Set once at start-up.
    /// </summary>
    public static string? ExecutablePath { get; set; } = Environment.ProcessPath;

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            string? exe = ExecutablePath;

            if (enabled && exe != null && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                key.SetValue(ValueName, $"\"{exe}\" {BackgroundArgument}");
            else if (!enabled)
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch
        {
            // Not critical: the setting is still saved and will be applied on the next change.
        }
    }

    public static bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }
}
