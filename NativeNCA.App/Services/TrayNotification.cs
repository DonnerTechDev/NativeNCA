using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace NativeNCA.App.Services;

/// <summary>
/// Shows a notification from the tray icon (a balloon, which Windows 11 shows as a toast) and reports clicks
/// on it. WPF-UI's NotifyIcon has no API for this, so the icon's shell data (window and id) is read from its
/// internal manager and the balloon is sent with Shell_NotifyIcon(NIM_MODIFY, NIF_INFO) for the same icon.
/// </summary>
internal sealed class TrayNotification
{
    private const int NIM_MODIFY = 0x1;
    private const int NIF_INFO = 0x10;
    private const int NIIF_INFO = 0x1;
    private const int NIIF_USER = 0x4;
    private const int NIN_BALLOONUSERCLICK = 0x405;

    private readonly object _notifyIcon;
    private HwndSource? _hookedSource;
    private uint _callbackMessage;

    public TrayNotification(object notifyIcon) => _notifyIcon = notifyIcon;

    /// <summary>The user clicked the notification.</summary>
    public event EventHandler? Clicked;

    /// <summary>Shows the notification; false if the tray icon is not registered or its data cannot be read.</summary>
    public bool Show(string title, string text)
    {
        if (!TryGetIconData(out var hwnd, out uint id, out uint callbackMessage, out var icon, out var source)) return false;

        if (source != null && !ReferenceEquals(source, _hookedSource))
        {
            _hookedSource?.RemoveHook(WndProc);
            _hookedSource = source;
            _callbackMessage = callbackMessage;
            source.AddHook(WndProc);
        }

        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = hwnd,
            uID = id,
            uFlags = NIF_INFO,
            szInfoTitle = Truncate(title, 63),
            szInfo = Truncate(text, 255),
            // The app icon (the tray icon itself) rather than the generic "i".
            dwInfoFlags = NIIF_USER,
            hBalloonIcon = icon
        };
        if (Shell_NotifyIconW(NIM_MODIFY, ref data)) return true;

        data.dwInfoFlags = NIIF_INFO;
        data.hBalloonIcon = IntPtr.Zero;
        return Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_callbackMessage != 0 && msg == _callbackMessage && (lParam.ToInt64() & 0xFFFF) == NIN_BALLOONUSERCLICK)
            Clicked?.Invoke(this, EventArgs.Empty);
        return IntPtr.Zero;
    }

    /// <summary>Finds the manager behind NotifyIcon (it implements Wpf.Ui.Tray.INotifyIcon) and reads its shell data.</summary>
    private bool TryGetIconData(out IntPtr hwnd, out uint id, out uint callbackMessage, out IntPtr icon, out HwndSource? source)
    {
        hwnd = icon = IntPtr.Zero;
        id = callbackMessage = 0;
        source = null;

        try
        {
            object? manager = null;
            for (var type = _notifyIcon.GetType(); type != null && manager == null; type = type.BaseType)
            {
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                {
                    var value = field.GetValue(_notifyIcon);
                    if (value?.GetType().GetProperty("ShellIconData") != null)
                    {
                        manager = value;
                        break;
                    }
                }
            }
            if (manager == null) return false;

            var managerType = manager.GetType();
            if (managerType.GetProperty("IsRegistered")?.GetValue(manager) is false) return false;

            object? shellData = managerType.GetProperty("ShellIconData")?.GetValue(manager);
            if (shellData == null) return false;
            var dataType = shellData.GetType();

            hwnd = (IntPtr)(dataType.GetField("hWnd")?.GetValue(shellData) ?? IntPtr.Zero);
            id = Convert.ToUInt32(dataType.GetField("uID")?.GetValue(shellData) ?? 0);
            callbackMessage = Convert.ToUInt32(dataType.GetField("uCallbackMessage")?.GetValue(shellData) ?? 0);
            icon = (IntPtr)(dataType.GetField("hIcon")?.GetValue(shellData) ?? IntPtr.Zero);
            source = managerType.GetProperty("HookWindow")?.GetValue(manager) as HwndSource ?? HwndSource.FromHwnd(hwnd);
            return hwnd != IntPtr.Zero;
        }
        catch
        {
            return false;
        }
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(int message, ref NOTIFYICONDATAW data);
}
