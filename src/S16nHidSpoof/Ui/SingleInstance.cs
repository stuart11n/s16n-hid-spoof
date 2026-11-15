using System.Runtime.InteropServices;

namespace S16nHidSpoof.Ui;

/// <summary>
/// One running instance (tray or window). A second launch activates the first instead of
/// colliding on HIDMaestro identity keys.
/// </summary>
internal static class SingleInstance
{
    private const string MutexName = @"Local\s16n-hid-spoof_single";
    private const string WindowTitle = "s16n HID Spoof";
    private static Mutex? _mutex;

    public static bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, name: MutexName, createdNew: out var created);
        if (created)
            return true;

        _mutex.Dispose();
        _mutex = null;
        return false;
    }

    public static void ActivateExisting()
    {
        var hwnd = FindWindowW(null, WindowTitle);
        if (hwnd == IntPtr.Zero)
            return;

        ShowWindow(hwnd, SwRestore);
        ShowWindow(hwnd, SwShow);
        SetForegroundWindow(hwnd);
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
