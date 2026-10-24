using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace NativeNCA.Core.Security;

/// <summary>The trusted root store browsers read. Tests use a fake: never touch the real store from tests.</summary>
public interface IRootCertificateStore
{
    bool Contains(X509Certificate2 certificate);

    /// <summary>Adds a root. Throws <see cref="OperationCanceledException"/> if the user declines the Windows prompt.</summary>
    void Add(X509Certificate2 certificate, IntPtr ownerWindow);

    /// <summary>Removes a root. Throws <see cref="OperationCanceledException"/> if the user declines the Windows prompt.</summary>
    void Remove(X509Certificate2 certificate, IntPtr ownerWindow);

    IReadOnlyList<X509Certificate2> FindByOrganization(string organization);
}

/// <summary>
/// CurrentUser\Root. Windows itself asks the user to confirm every addition and removal there,
/// so Add and Remove block until that security prompt is answered: call them off the UI thread.
/// </summary>
public sealed class CurrentUserRootStore : IRootCertificateStore
{
    private const int ErrorCancelled = unchecked((int)0x800704C7);

    public bool Contains(X509Certificate2 certificate)
    {
        using var store = Open(OpenFlags.ReadOnly);
        return store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, false).Count > 0;
    }

    public void Add(X509Certificate2 certificate, IntPtr ownerWindow)
    {
        using var store = Open(OpenFlags.ReadWrite);
        RunWithPrompt(() => store.Add(certificate), ownerWindow);
        if (!Contains(certificate)) throw new OperationCanceledException();
    }

    public void Remove(X509Certificate2 certificate, IntPtr ownerWindow)
    {
        using var store = Open(OpenFlags.ReadWrite);
        RunWithPrompt(() => store.Remove(certificate), ownerWindow);
        if (Contains(certificate)) throw new OperationCanceledException();
    }

    public IReadOnlyList<X509Certificate2> FindByOrganization(string organization)
    {
        using var store = Open(OpenFlags.ReadOnly);
        return store.Certificates
            .Where(c => c.SubjectName.EnumerateRelativeDistinguishedNames()
                .Any(rdn => rdn.GetSingleElementType().Value == "2.5.4.10" && rdn.GetSingleElementValue() == organization))
            .ToList();
    }

    private static X509Store Open(OpenFlags flags)
    {
        var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(flags);
        return store;
    }

    private static void RunWithPrompt(Action action, IntPtr ownerWindow)
    {
        using var done = new CancellationTokenSource();
        var watcher = Task.Run(() => RaisePromptAsync(ownerWindow, done.Token));
        try
        {
            action();
        }
        catch (CryptographicException ex) when (ex.HResult == ErrorCancelled)
        {
            throw new OperationCanceledException("Windows prompt declined", ex);
        }
        finally
        {
            done.Cancel();
            try { watcher.Wait(); } catch { /* best effort only */ }
        }
    }

    // ===================== Keeping the Windows prompt in front =====================

    /// <summary>
    /// The security prompt is a plain dialog crypt32 creates in this process without an owner, so Windows
    /// may put it behind our window (it then hides in the same taskbar group). While the store call runs,
    /// attach any such dialog to our window and bring it to the front. We are the foreground process at
    /// that moment (the user just clicked a button), so SetForegroundWindow is allowed.
    /// </summary>
    private static async Task RaisePromptAsync(IntPtr ownerWindow, CancellationToken ct)
    {
        var raised = new HashSet<IntPtr>();
        while (!ct.IsCancellationRequested)
        {
            foreach (IntPtr dialog in FindOwnDialogs())
            {
                if (!raised.Add(dialog)) continue;
                if (ownerWindow != IntPtr.Zero && dialog != ownerWindow)
                    SetWindowLongPtr(dialog, GwlpHwndParent, ownerWindow);
                SetForegroundWindow(dialog);
            }

            try { await Task.Delay(50, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Visible top-level standard dialogs (class #32770) of this process.</summary>
    private static List<IntPtr> FindOwnDialogs()
    {
        var result = new List<IntPtr>();
        uint pid = (uint)Environment.ProcessId;
        var className = new StringBuilder(32);
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint owner);
            if (owner == pid && IsWindowVisible(hwnd))
            {
                className.Clear();
                if (GetClassName(hwnd, className, className.Capacity) > 0 && className.ToString() == "#32770")
                    result.Add(hwnd);
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private const int GwlpHwndParent = -8;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
