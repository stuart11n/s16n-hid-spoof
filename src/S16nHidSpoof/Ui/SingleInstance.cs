using System.Diagnostics;
using System.Runtime.InteropServices;

namespace S16nHidSpoof.Ui;

/// <summary>
/// One running instance (tray or window). Uses a live process check — not a sticky
/// named mutex — so Environment.Exit / crashes cannot block the next launch.
/// </summary>
internal static class SingleInstance
{
    private const string WindowTitle = "s16n HID Spoof";
    private static Mutex? _mutex;

    public static bool TryAcquire()
    {
        // If another process is alive, refuse. Otherwise take (or reclaim) the mutex.
        if (OtherProcessAlive())
            return false;

        _mutex = new Mutex(initiallyOwned: true, name: @"Local\s16n-hid-spoof_single", createdNew: out var created);
        if (created)
            return true;

        try
        {
            if (_mutex.WaitOne(0))
                return true;
        }
        catch (AbandonedMutexException)
        {
            return true;
        }

        // Mutex busy but no process — force reclaim by abandoning our handle and retrying once.
        _mutex.Dispose();
        _mutex = new Mutex(initiallyOwned: true, name: @"Local\s16n-hid-spoof_single", createdNew: out created);
        if (created)
            return true;

        try
        {
            if (_mutex.WaitOne(TimeSpan.FromMilliseconds(200)))
                return true;
        }
        catch (AbandonedMutexException)
        {
            return true;
        }

        _mutex.Dispose();
        _mutex = null;
        // No live process: allow start anyway (mutex was a false positive).
        _mutex = new Mutex(initiallyOwned: false);
        return true;
    }

    public static void ActivateExisting()
    {
        foreach (var proc in Process.GetProcessesByName("s16n-hid-spoof"))
        {
            try
            {
                if (proc.Id == Environment.ProcessId)
                    continue;
                var hwnd = proc.MainWindowHandle;
                if (hwnd == IntPtr.Zero)
                    hwnd = FindWindowW(null, WindowTitle);
                if (hwnd == IntPtr.Zero)
                    continue;

                ShowWindow(hwnd, SwRestore);
                ShowWindow(hwnd, SwShow);
                SetForegroundWindow(hwnd);
                return;
            }
            catch
            {
                /* ignore access / exited */
            }
            finally
            {
                proc.Dispose();
            }
        }

        var fallback = FindWindowW(null, WindowTitle);
        if (fallback == IntPtr.Zero)
            return;

        ShowWindow(fallback, SwRestore);
        ShowWindow(fallback, SwShow);
        SetForegroundWindow(fallback);
    }

    public static bool OtherProcessAlive()
    {
        foreach (var proc in Process.GetProcessesByName("s16n-hid-spoof"))
        {
            try
            {
                if (proc.Id != Environment.ProcessId)
                    return true;
            }
            catch
            {
                /* ignore */
            }
            finally
            {
                proc.Dispose();
            }
        }

        return false;
    }

    public static void Release()
    {
        try
        {
            _mutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            /* not owned */
        }

        _mutex?.Dispose();
        _mutex = null;
    }

    private const int SwRestore = 9;
    private const int SwShow = 5;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
