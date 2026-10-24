using System.Diagnostics;
using System.Text.Json;
using NativeNCA.Core.Localization;

namespace NativeNCA.Core.Updates;

/// <summary>
/// Files of an installed NativeNCA in <c>%LOCALAPPDATA%\Programs\NativeNCA</c>, and the renames that update
/// and roll back the running exe. Windows lets a running exe be renamed but not deleted or overwritten,
/// so an update moves <c>NativeNCA.exe</c> aside as <c>NativeNCA.prev.exe</c> (kept for rollback, with its
/// version in <c>NativeNCA.prev.json</c>) and moves the verified <c>NativeNCA.new.exe</c> into its place.
/// Every step that fails puts the earlier names back.
/// </summary>
public sealed class AppInstallation
{
    public const string ExeName = "NativeNCA.exe";
    public const string NewExeName = "NativeNCA.new.exe";
    public const string PreviousExeName = "NativeNCA.prev.exe";
    public const string PreviousInfoName = "NativeNCA.prev.json";

    /// <summary>A rolled-back (newer) exe that was still running; deleted on the next start.</summary>
    public const string DiscardedExeName = "NativeNCA.old.exe";

    public AppInstallation(string? directory = null)
    {
        Directory = directory ?? DefaultDirectory;
    }

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "NativeNCA");

    public string Directory { get; }
    public string ExePath => Path.Combine(Directory, ExeName);
    public string NewExePath => Path.Combine(Directory, NewExeName);
    public string PreviousExePath => Path.Combine(Directory, PreviousExeName);
    public string PreviousInfoPath => Path.Combine(Directory, PreviousInfoName);
    public string DiscardedExePath => Path.Combine(Directory, DiscardedExeName);

    public bool IsInstalled => File.Exists(ExePath);

    /// <summary>True when <paramref name="processPath"/> is this installation's NativeNCA.exe.</summary>
    public bool IsInstalledCopy(string? processPath) =>
        processPath != null && string.Equals(Path.GetFullPath(processPath), Path.GetFullPath(ExePath), StringComparison.OrdinalIgnoreCase);

    /// <summary>Version of the installed exe (from its file version resource), null if not installed or unreadable.</summary>
    public AppVersion? GetInstalledVersion() => ReadExeVersion(ExePath);

    public static AppVersion? ReadExeVersion(string path)
    {
        try
        {
            return File.Exists(path) && AppVersion.TryParse(FileVersionInfo.GetVersionInfo(path).ProductVersion, out var v) ? v : null;
        }
        catch
        {
            return null;
        }
    }

    // ===================== Previous version =====================

    public bool HasPreviousVersion => File.Exists(PreviousExePath);

    /// <summary>Version kept for rollback: from the json, otherwise from the exe itself.</summary>
    public AppVersion? GetPreviousVersion()
    {
        if (!HasPreviousVersion) return null;
        try
        {
            if (File.Exists(PreviousInfoPath))
            {
                var info = JsonSerializer.Deserialize<PreviousInfo>(File.ReadAllText(PreviousInfoPath));
                if (AppVersion.TryParse(info?.Version, out var v)) return v;
            }
        }
        catch
        {
            // Fall back to the exe's version resource.
        }
        return ReadExeVersion(PreviousExePath);
    }

    private sealed record PreviousInfo(string Version);

    private void WritePreviousInfo(AppVersion version)
    {
        try
        {
            File.WriteAllText(PreviousInfoPath, JsonSerializer.Serialize(new PreviousInfo(version.ToString())));
        }
        catch
        {
            // The exe's own version resource is the fallback.
        }
    }

    private void DeletePreviousInfo()
    {
        try { File.Delete(PreviousInfoPath); } catch { /* stale info is ignored without the exe */ }
    }

    // ===================== Update =====================

    /// <summary>
    /// Replaces <c>NativeNCA.exe</c> (possibly running, version <paramref name="currentVersion"/>) with the
    /// staged <c>NativeNCA.new.exe</c>. The replaced exe becomes the previous version; an older previous
    /// version is deleted first. On failure every rename is undone and the exception is rethrown.
    /// </summary>
    public void SwapInNewVersion(AppVersion currentVersion)
    {
        if (!File.Exists(NewExePath)) throw new FileNotFoundException(Strings.Install_NewVersionMissing, NewExePath);
        if (!File.Exists(ExePath)) throw new FileNotFoundException(Strings.Install_NotInstalled, ExePath);

        // Keep exactly one previous version.
        if (File.Exists(PreviousExePath)) File.Delete(PreviousExePath);
        DeletePreviousInfo();

        File.Move(ExePath, PreviousExePath);
        try
        {
            File.Move(NewExePath, ExePath);
        }
        catch
        {
            File.Move(PreviousExePath, ExePath);
            throw;
        }

        WritePreviousInfo(currentVersion);
    }

    /// <summary>Undoes <see cref="SwapInNewVersion"/> when the new exe failed to start.</summary>
    public void UndoSwap()
    {
        if (!File.Exists(PreviousExePath)) return;
        if (File.Exists(ExePath))
        {
            if (File.Exists(NewExePath)) File.Delete(NewExePath);
            File.Move(ExePath, NewExePath);
        }
        File.Move(PreviousExePath, ExePath);
        DeletePreviousInfo();
    }

    // ===================== Rollback =====================

    /// <summary>
    /// Puts the previous version back as <c>NativeNCA.exe</c>. The current (newer) exe may be running, so it
    /// is renamed to <c>NativeNCA.old.exe</c> and deleted by <see cref="CleanUp"/> on the next start. After a
    /// rollback there is no previous version until the next update.
    /// </summary>
    public void SwapToPreviousVersion()
    {
        if (!File.Exists(PreviousExePath)) throw new FileNotFoundException(Strings.Install_PreviousMissing, PreviousExePath);

        if (File.Exists(DiscardedExePath)) File.Delete(DiscardedExePath);

        bool hadCurrent = File.Exists(ExePath);
        if (hadCurrent) File.Move(ExePath, DiscardedExePath);
        try
        {
            File.Move(PreviousExePath, ExePath);
        }
        catch
        {
            if (hadCurrent) File.Move(DiscardedExePath, ExePath);
            throw;
        }

        DeletePreviousInfo();
    }

    /// <summary>Undoes <see cref="SwapToPreviousVersion"/> when the previous exe failed to start.</summary>
    public void UndoRollback(AppVersion previousVersion)
    {
        if (!File.Exists(DiscardedExePath)) return;
        if (File.Exists(ExePath)) File.Move(ExePath, PreviousExePath);
        File.Move(DiscardedExePath, ExePath);
        WritePreviousInfo(previousVersion);
    }

    /// <summary>Removes leftovers of earlier updates (best effort: a file may still be in use).</summary>
    public void CleanUp()
    {
        foreach (string path in new[] { DiscardedExePath, NewExePath })
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* next time */ }
        }
        if (!HasPreviousVersion) DeletePreviousInfo();
    }
}
