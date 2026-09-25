using System.Diagnostics;
using System.Runtime.InteropServices;
using GlassesRemote.Server.Desktop;

namespace GlassesRemote.Server.Windows;

/// <summary>
/// Finds the most recently used top-level window of an app (EnumWindows walks the Z-order,
/// front to back), restores it, fits its visible frame to the cast area and brings it to the
/// front. Windows of elevated apps can't be moved from here (UIPI) and report Failed.
/// </summary>
public sealed partial class Win32WindowSwitcher(ILogger<Win32WindowSwitcher> logger) : IWindowSwitcher
{
    private const int SW_RESTORE = 9;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const uint GW_OWNER = 4;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int DWMWA_CLOAKED = 14;

    public AppSwitchResult Switch(AppShortcut app, CaptureRegion area)
    {
        var window = FindWindow(app);
        if (window == 0)
        {
            return AppSwitchResult.NotRunning;
        }

        try
        {
            if (IsIconic(window) || IsZoomed(window))
            {
                ShowWindow(window, SW_RESTORE);
            }

            var screen = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
            var target = new Rect(screen.X + area.X, screen.Y + area.Y,
                screen.X + area.X + area.Width, screen.Y + area.Y + area.Height);

            // Windows 10/11 frames have invisible resize borders: grow the window rect by them
            // so the visible edge lands on the cast area.
            var outer = new Rect();
            var visible = new Rect();
            if (GetWindowRect(window, out outer)
                && DwmGetWindowAttribute(window, DWMWA_EXTENDED_FRAME_BOUNDS, out visible, Marshal.SizeOf<Rect>()) == 0)
            {
                target = new Rect(
                    target.Left - (visible.Left - outer.Left),
                    target.Top - (visible.Top - outer.Top),
                    target.Right + (outer.Right - visible.Right),
                    target.Bottom + (outer.Bottom - visible.Bottom));
            }

            var moved = SetWindowPos(window, 0, target.Left, target.Top, target.Right - target.Left,
                target.Bottom - target.Top, SWP_NOZORDER | SWP_NOACTIVATE);
            var focused = BringToFront(window);
            return moved && focused ? AppSwitchResult.Switched : AppSwitchResult.Failed;
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Switching to {App} failed", app.Name);
            return AppSwitchResult.Failed;
        }
    }

    private static nint FindWindow(AppShortcut app)
    {
        var process = app.Process?.Trim();
        var title = app.Title?.Trim();
        var names = new Dictionary<uint, string>();
        nint found = 0;

        EnumWindows((hwnd, _) =>
        {
            if (!IsAppWindow(hwnd))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(title) && !WindowTitle(hwnd).Contains(title, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(process))
            {
                GetWindowThreadProcessId(hwnd, out var pid);
                if (!names.TryGetValue(pid, out var name))
                {
                    name = ProcessName(pid);
                    names[pid] = name;
                }

                if (!string.Equals(name, process, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            found = hwnd;
            return false; // first match in Z-order = most recently used
        }, 0);

        return found;
    }

    /// <summary>A visible, titled, unowned, non-tool, non-cloaked top-level window: what Alt+Tab shows.</summary>
    private static bool IsAppWindow(nint hwnd)
    {
        if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != 0 || GetWindowTextLength(hwnd) == 0)
        {
            return false;
        }

        if ((GetWindowLongPtr(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0)
        {
            return false;
        }

        return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) != 0 || cloaked == 0;
    }

    private static string WindowTitle(nint hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        var buffer = new char[length + 1];
        var copied = GetWindowText(hwnd, buffer, buffer.Length);
        return new string(buffer, 0, copied);
    }

    private static string ProcessName(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return "";
        }
    }

    /// <summary>
    /// SetForegroundWindow is refused unless this process owns the foreground; attaching to the
    /// foreground thread's input queue for the call is the standard way round that.
    /// </summary>
    private static bool BringToFront(nint window)
    {
        if (SetForegroundWindow(window))
        {
            return true;
        }

        var foreground = GetForegroundWindow();
        var foregroundThread = GetWindowThreadProcessId(foreground, out _);
        var thisThread = GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != thisThread
                       && AttachThreadInput(thisThread, foregroundThread, true);
        try
        {
            BringWindowToTop(window);
            return SetForegroundWindow(window);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(thisThread, foregroundThread, false);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Rect(int left, int top, int right, int bottom)
    {
        public readonly int Left = left;
        public readonly int Top = top;
        public readonly int Right = right;
        public readonly int Bottom = bottom;
    }

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindow(nint hwnd, uint cmd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    private static partial int GetWindowTextLength(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetWindowText(nint hwnd, [Out] char[] text, int maxCount);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial long GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsZoomed(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hwnd, int cmd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hwnd, out Rect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BringWindowToTop(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool doAttach);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(nint hwnd, int attribute, out Rect value, int size);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
}
