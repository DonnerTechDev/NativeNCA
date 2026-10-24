using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Win32;
using NativeNCA.Core.Security;

namespace NativeNCA.Core.Updates;

/// <summary>
/// Per-user installation without administrator rights: the exe in <c>%LOCALAPPDATA%\Programs\NativeNCA</c>,
/// a Start menu shortcut and an entry in "Installed apps" (HKCU Uninstall key) whose uninstall command is
/// <c>NativeNCA.exe --uninstall</c>.
/// </summary>
public sealed class SetupService
{
    public const string UninstallArgument = "--uninstall";
    public const string Publisher = "DonnerTechDev";
    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\NativeNCA";

    public SetupService(AppInstallation installation) => Installation = installation;

    public AppInstallation Installation { get; }

    public static string ShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "NativeNCA.lnk");

    /// <summary>All per-user data: vault, TLS files, update state.</summary>
    public static string UserDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NativeNCA");

    /// <summary>
    /// Copies <paramref name="sourceExe"/> into the install folder and registers it. Over an older installation
    /// the installed exe becomes the previous version, exactly as after an update. The installed copy must not
    /// be running (its server would hold the port).
    /// </summary>
    public void Install(string sourceExe, AppVersion version)
    {
        Directory.CreateDirectory(Installation.Directory);

        if (Installation.IsInstalled)
        {
            File.Copy(sourceExe, Installation.NewExePath, overwrite: true);
            Installation.SwapInNewVersion(Installation.GetInstalledVersion() ?? AppVersion.Parse("0.0.0"));
        }
        else
        {
            File.Copy(sourceExe, Installation.ExePath, overwrite: false);
        }

        Register(version);

        // An existing autostart entry follows the program to its new place.
        AutoStartService.ExecutablePath = Installation.ExePath;
        if (AutoStartService.IsRegistered()) AutoStartService.Apply(true);
    }

    /// <summary>Writes the shortcut and the Uninstall entry (also refreshes the version after an update).</summary>
    public void Register(AppVersion version)
    {
        string exe = Installation.ExePath;
        using (var key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath, writable: true))
        {
            key.SetValue("DisplayName", "NativeNCA");
            key.SetValue("DisplayVersion", version.ToString());
            key.SetValue("Publisher", Publisher);
            key.SetValue("DisplayIcon", $"\"{exe}\",0");
            key.SetValue("InstallLocation", Installation.Directory);
            key.SetValue("UninstallString", $"\"{exe}\" {UninstallArgument}");
            key.SetValue("QuietUninstallString", $"\"{exe}\" {UninstallArgument}");
            key.SetValue("URLInfoAbout", $"https://github.com/{GitHubReleaseClient.Repository}");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            long sizeKb = File.Exists(exe) ? new FileInfo(exe).Length / 1024 : 0;
            key.SetValue("EstimatedSize", (int)Math.Min(sizeKb, int.MaxValue), RegistryValueKind.DWord);
        }

        CreateShortcut(ShortcutPath, exe, "Подписание ЭЦП НУЦ РК на сайтах — замена NCALayer");
    }

    public static bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
        return key != null;
    }

    /// <summary>Version written to "Installed apps", to refresh it after an update.</summary>
    public static string? GetRegisteredVersion()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
        return key?.GetValue("DisplayVersion") as string;
    }

    /// <summary>Removes the shortcut, the Uninstall entry and autostart.</summary>
    public void Unregister()
    {
        AutoStartService.Apply(false);
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false); } catch { /* best effort */ }
        try { if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath); } catch { /* best effort */ }
    }

    /// <summary>
    /// Deletes the install folder once this process has exited: a running exe cannot delete itself, so a hidden
    /// <c>cmd</c> retries for about half a minute. It runs from %TEMP% so it does not hold the folder open.
    /// </summary>
    public void ScheduleDirectoryRemoval()
    {
        string dir = Installation.Directory.TrimEnd('\\');
        if (!Directory.Exists(dir)) return;
        if (dir.Contains('"') || dir.Contains('%')) throw new InvalidOperationException("Недопустимый путь установки");

        string script =
            $"for /L %i in (1,1,30) do @(ping -n 2 127.0.0.1 >nul & rmdir /s /q \"{dir}\" 2>nul & if not exist \"{dir}\" exit /b 0)";
        Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = $"/d /q /s /c \"{script}\"",
            WorkingDirectory = Path.GetTempPath(),
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    /// <summary>Deletes the vault, TLS files and update state. The TLS root must be removed from Windows first.</summary>
    public static void DeleteUserData()
    {
        if (Directory.Exists(UserDataDirectory))
            Directory.Delete(UserDataDirectory, recursive: true);
    }

    // ===================== Shell link =====================

    internal static void CreateShortcut(string path, string target, string description)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var link = (IShellLinkW)new CShellLink();
        try
        {
            link.SetPath(target);
            link.SetWorkingDirectory(Path.GetDirectoryName(target)!);
            link.SetDescription(description);
            link.SetIconLocation(target, 0);
            ((IPersistFile)link).Save(path, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file, int maxPath, IntPtr findData, int flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder dir, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder args, int maxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder iconPath, int maxIconPath, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, int reserved);
        void Resolve(IntPtr hwnd, int flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
