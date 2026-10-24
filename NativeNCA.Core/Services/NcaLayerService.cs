using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace NativeNCA.Core.Services;

/// <summary>One way NCALayer is started at sign-in.</summary>
/// <param name="Location">Run key value or Startup folder shortcut.</param>
/// <param name="Name">Registry value name or shortcut file name.</param>
/// <param name="IsMachineWide">HKLM or the all-users Startup folder: changing it needs administrator rights.</param>
/// <param name="ApprovedKeyPath">StartupApproved key that holds the entry's on/off state.</param>
public sealed record NcaLayerAutostartEntry(NcaLayerAutostartLocation Location, string Name, bool IsMachineWide, string ApprovedKeyPath);

public enum NcaLayerAutostartLocation { RunKey, StartupFolder }

/// <summary>
/// The original NCALayer: both programs need port 13579 and websites hardcode it, so only one can run.
/// NCALayer is a launcher (NCALayer.exe) that starts its bundled jre\bin\javaw.exe; the Java process owns the port.
/// The installer adds an NCALayer.lnk shortcut to the Startup folder.
/// </summary>
public static class NcaLayerService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedRunKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ApprovedRun32Key = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";
    private const string ApprovedStartupFolderKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

    /// <summary>Folders NCALayer is installed to (Program Files and the uninstall entry's InstallLocation).</summary>
    public static IReadOnlyList<string> GetInstallDirectories()
    {
        var dirs = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NCALayer"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "NCALayer")
        };

        foreach (var (hive, view) in new[] { (RegistryHive.CurrentUser, RegistryView.Default), (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32) })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall == null) continue;
                foreach (string name in uninstall.GetSubKeyNames())
                {
                    using var entry = uninstall.OpenSubKey(name);
                    if (entry?.GetValue("DisplayName") is string display && display.StartsWith("NCALayer", StringComparison.OrdinalIgnoreCase)
                        && entry.GetValue("InstallLocation") is string location && location.Length > 0)
                        dirs.Add(location);
                }
            }
            catch
            {
                // Registry view not available: the default folders are enough.
            }
        }

        return dirs
            .Select(d => Path.TrimEndingDirectorySeparator(Path.GetFullPath(d)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ===================== Processes =====================

    /// <summary>The process listening on 127.0.0.1:<paramref name="port"/>, if any.</summary>
    public static int? FindListeningProcessId(int port)
    {
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
        IntPtr table = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(table, ref size, false, AfInet, TcpTableOwnerPidListener, 0) != 0) return null;

            int count = Marshal.ReadInt32(table);
            IntPtr row = table + 4;
            int rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            for (int i = 0; i < count; i++, row += rowSize)
            {
                var r = Marshal.PtrToStructure<MibTcpRowOwnerPid>(row);
                int localPort = (int)(((r.LocalPort & 0xFF) << 8) | ((r.LocalPort >> 8) & 0xFF)); // network byte order
                if (localPort == port) return (int)r.OwningPid;
            }
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }
    }

    /// <summary>Running NCALayer processes: the launcher and its bundled Java.</summary>
    public static IReadOnlyList<Process> FindRunningProcesses()
    {
        var dirs = GetInstallDirectories();
        var result = new List<Process>();
        foreach (var process in Process.GetProcesses())
        {
            if (IsNcaLayerProcess(process, dirs)) result.Add(process);
            else process.Dispose();
        }
        return result;
    }

    /// <summary>Whether NCALayer is what holds <paramref name="port"/> (or, if the owner cannot be read, is running at all).</summary>
    public static bool IsPortOwnedByNcaLayer(int port)
    {
        var dirs = GetInstallDirectories();
        int? pid = FindListeningProcessId(port);
        if (pid != null)
        {
            try
            {
                using var owner = Process.GetProcessById(pid.Value);
                if (IsNcaLayerProcess(owner, dirs)) return true;
                // A path we could read that is not NCALayer: some other program.
                if (GetImagePath(owner.Id) != null) return false;
            }
            catch (ArgumentException)
            {
                // Exited in the meantime.
            }
        }

        var running = FindRunningProcesses();
        foreach (var p in running) p.Dispose();
        return running.Count > 0;
    }

    /// <summary>Name of the program holding the port, for the error message.</summary>
    public static string? GetPortOwnerName(int port)
    {
        if (FindListeningProcessId(port) is not { } pid) return null;
        try
        {
            using var process = Process.GetProcessById(pid);
            return GetImagePath(pid) is { } path ? Path.GetFileName(path) : process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Terminates NCALayer and waits until the port is free. Returns false if it is still taken.</summary>
    public static async Task<bool> CloseAsync(int port, TimeSpan timeout)
    {
        var processes = FindRunningProcesses();
        try
        {
            // Java first: it owns the port. The launcher just waits for it.
            foreach (var process in processes.OrderBy(p => p.ProcessName.StartsWith("java", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { /* already gone */ }
            }

            using var cts = new CancellationTokenSource(timeout);
            foreach (var process in processes)
            {
                try { await process.WaitForExitAsync(cts.Token); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }

        var deadline = DateTime.UtcNow + timeout;
        while (FindListeningProcessId(port) != null)
        {
            if (DateTime.UtcNow > deadline) return false;
            await Task.Delay(150);
        }
        return true;
    }

    internal static bool IsNcaLayerProcess(Process process, IReadOnlyList<string> installDirs)
    {
        if (process.ProcessName.Equals("NCALayer", StringComparison.OrdinalIgnoreCase)) return true;

        string? path = GetImagePath(process.Id);
        return path != null && installDirs.Any(dir =>
            path.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetImagePath(int pid)
    {
        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    // ===================== Autostart =====================

    /// <summary>Enabled NCALayer autostart entries (Run keys and Startup folders, honouring Task Manager's on/off state).</summary>
    public static IReadOnlyList<NcaLayerAutostartEntry> FindAutostartEntries()
    {
        var entries = new List<NcaLayerAutostartEntry>();

        foreach (var (hive, machine) in new[] { (Registry.CurrentUser, false), (Registry.LocalMachine, true) })
        {
            foreach (var view in machine ? new[] { RegistryView.Registry64, RegistryView.Registry32 } : new[] { RegistryView.Default })
            {
                try
                {
                    using var root = RegistryKey.OpenBaseKey(machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, view);
                    using var run = root.OpenSubKey(RunKey);
                    if (run == null) continue;
                    string approved = view == RegistryView.Registry32 ? ApprovedRun32Key : ApprovedRunKey;
                    foreach (string name in run.GetValueNames())
                    {
                        if (run.GetValue(name) is string command && MentionsNcaLayer(name + " " + command) && IsApproved(hive, approved, name))
                            entries.Add(new NcaLayerAutostartEntry(NcaLayerAutostartLocation.RunKey, name, machine, approved));
                    }
                }
                catch
                {
                    // Unreadable key: nothing to report from it.
                }
            }
        }

        foreach (var (folder, hive, machine) in new[]
                 {
                     (Environment.GetFolderPath(Environment.SpecialFolder.Startup), Registry.CurrentUser, false),
                     (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), Registry.LocalMachine, true)
                 })
        {
            if (!Directory.Exists(folder)) continue;
            foreach (string file in Directory.EnumerateFiles(folder))
            {
                string name = Path.GetFileName(file);
                if ((MentionsNcaLayer(name) || ShortcutMentionsNcaLayer(file)) && IsApproved(hive, ApprovedStartupFolderKey, name))
                    entries.Add(new NcaLayerAutostartEntry(NcaLayerAutostartLocation.StartupFolder, name, machine, ApprovedStartupFolderKey));
            }
        }

        return entries;
    }

    /// <summary>
    /// Turns the entries off the way Task Manager's "Startup apps" does (StartupApproved), so the user
    /// can turn them back on there. Machine-wide entries need administrator rights and are returned
    /// as not changed.
    /// </summary>
    public static IReadOnlyList<NcaLayerAutostartEntry> DisableAutostart(IEnumerable<NcaLayerAutostartEntry> entries)
    {
        var notChanged = new List<NcaLayerAutostartEntry>();
        foreach (var entry in entries)
        {
            try
            {
                var hive = entry.IsMachineWide ? Registry.LocalMachine : Registry.CurrentUser;
                using var key = hive.CreateSubKey(entry.ApprovedKeyPath, writable: true);
                key.SetValue(entry.Name, DisabledMarker(DateTime.UtcNow), RegistryValueKind.Binary);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                notChanged.Add(entry);
            }
        }
        return notChanged;
    }

    /// <summary>StartupApproved value: first byte odd = disabled, followed by the FILETIME it was turned off.</summary>
    internal static byte[] DisabledMarker(DateTime utc)
    {
        var data = new byte[12];
        data[0] = 0x03;
        BitConverter.GetBytes(utc.ToFileTimeUtc()).CopyTo(data, 4);
        return data;
    }

    internal static bool IsEnabledMarker(byte[]? data) => data == null || data.Length == 0 || (data[0] & 1) == 0;

    private static bool IsApproved(RegistryKey hive, string approvedKey, string name)
    {
        try
        {
            using var key = hive.OpenSubKey(approvedKey);
            return IsEnabledMarker(key?.GetValue(name) as byte[]);
        }
        catch
        {
            return true;
        }
    }

    internal static bool MentionsNcaLayer(string text) => text.Contains("NCALayer", StringComparison.OrdinalIgnoreCase);

    /// <summary>A .lnk stores its target path as ANSI and/or UTF-16 text; that is enough to recognise NCALayer.</summary>
    internal static bool ShortcutMentionsNcaLayer(string path)
    {
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > 1024 * 1024) return false;
            byte[] data = File.ReadAllBytes(path);
            return MentionsNcaLayer(Encoding.Latin1.GetString(data)) || MentionsNcaLayer(Encoding.Unicode.GetString(data))
                || (data.Length > 1 && MentionsNcaLayer(Encoding.Unicode.GetString(data, 1, data.Length - 1)));
        }
        catch
        {
            return false;
        }
    }

    // ===================== Interop =====================

    private const int AfInet = 2;
    private const int TcpTableOwnerPidListener = 3;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
