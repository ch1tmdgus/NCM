using System.ComponentModel;
using System.Runtime.InteropServices;

namespace NcmX;

// A painted layered window receives WM_SETCURSOR across the entire virtual desktop.
// Its cursor belongs to this window; no persistent system cursor settings are changed.
internal sealed class ColorPickerOverlay : IDisposable
{
    private readonly string className = "NcmX.ColorPicker." + Guid.NewGuid().ToString("N");
    private readonly WindowProcedure procedure;
    private readonly nint instance = GetModuleHandleW(null);
    private nint window, crosshair, previousCursor;
    private ushort atom;

    internal ColorPickerOverlay() { procedure = WndProc; }

    public void Show()
    {
        Hide();
        crosshair = LoadCursorW(0, new nint(32515)); // IDC_CROSS
        if (crosshair == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (atom == 0) {
            var registration = new WindowClass {
                Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = procedure,
                Instance = instance, Cursor = crosshair,
                Background = GetStockObject(4), ClassName = className
            };
            atom = RegisterClassExW(ref registration);
            if (atom == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        int left = GetSystemMetrics(76), top = GetSystemMetrics(77);
        int width = GetSystemMetrics(78), height = GetSystemMetrics(79);
        window = CreateWindowExW(0x00080000 | 0x08000000 | 0x00000008,
            className, "NCM Color Picker", 0x80000000, left, top, width, height, 0, 0, instance, 0);
        if (window == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        previousCursor = GetCursor();
        if (!SetLayeredWindowAttributes(window, 0, 1, 2)) {
            int error = Marshal.GetLastWin32Error(); Hide(); throw new Win32Exception(error);
        }
        SetWindowPos(window, new nint(-1), left, top, width, height, 0x50);
        UpdateWindow(window);
        SetCursor(crosshair);
        StartupDiagnostics.Write("Color picker: overlay shown; native IDC_CROSS selected");
    }

    internal bool VerifyCrosshair()
    {
        var info = new CursorInfo { Size = (uint)Marshal.SizeOf<CursorInfo>() };
        return window != 0 && IsWindowVisible(window) && GetCursorInfo(ref info) && info.Cursor == crosshair;
    }

    private nint WndProc(nint handle, uint message, nint wParam, nint lParam)
    {
        if (message == 0x0020) { SetCursor(crosshair); return 1; } // WM_SETCURSOR
        if (message == 0x0084) return 1; // WM_NCHITTEST: HTCLIENT
        if (message == 0x0021) return 3; // WM_MOUSEACTIVATE: MA_NOACTIVATE
        return DefWindowProcW(handle, message, wParam, lParam);
    }

    public void Hide()
    {
        if (window == 0) return;
        DestroyWindow(window); window = 0;
        SetCursor(previousCursor != 0 ? previousCursor : LoadCursorW(0, new nint(32512)));
    }
    public void Dispose()
    {
        Hide();
        if (atom != 0) { UnregisterClassW(className, instance); atom = 0; }
        GC.KeepAlive(procedure);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProcedure(nint window, uint message, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass {
        public uint Size, Style;
        public WindowProcedure Procedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        public string? MenuName;
        public string ClassName;
        public nint SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo { public uint Size, Flags; public nint Cursor; public int X, Y; }
    [DllImport("user32.dll", SetLastError = true, ExactSpelling = true)] private static extern ushort RegisterClassExW(ref WindowClass registration);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern bool UnregisterClassW(string name, nint instance);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern nint GetModuleHandleW(string? name);
    [DllImport("gdi32.dll")] private static extern nint GetStockObject(int index);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)] private static extern nint CreateWindowExW(
        uint exStyle, string className, string title, uint style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetLayeredWindowAttributes(nint window, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool UpdateWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint SetCursor(nint cursor);
    [DllImport("user32.dll")] private static extern nint GetCursor();
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll", SetLastError = true, ExactSpelling = true)] private static extern nint LoadCursorW(nint instance, nint name);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
}
