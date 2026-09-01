using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using ETab.Helpers;

namespace ETab.WinAPI;

public enum Win11Backdrop
{
    Mica,
    MicaAlt,
    Acrylic,
}

public static class WinApi
{
    public const int EVENT_OBJECT_CREATE = 0x8000;
    public const int EVENT_OBJECT_SHOW = 0x8002;
    public const int WM_COMMAND = 0x111;
    public const int SW_HIDE = 0;
    public const int SW_SHOWNOACTIVATE = 4;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_HIDEWINDOW = 0x0080;

    public const uint SIGDN_URL = 0x80068000;

    // DWM attributes for Windows 11 system backdrop / rounded corners.
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    public const int DWMSBT_MAINWINDOW = 2; // Mica
    public const int DWMSBT_TRANSIENTWINDOW = 3; // Acrylic (temporary window)
    public const int DWMSBT_TABBEDWINDOW = 4; // Mica Alt (tabbed windows, e.g. File Explorer)
    public const int DWMWCP_ROUND = 2;

    public const int GWL_STYLE = -16;
    public const long WS_CAPTION = 0x00C00000L;
    public const long WS_SYSMENU = 0x00080000L;

    public const int WTA_NONCLIENT = 1;
    public const uint WTNCA_NODRAWCAPTION = 0x1;
    public const uint WTNCA_NODRAWICON = 0x2;
    public const uint WTNCA_NOMIRRORHELP = 0x8;
    public const uint WTNCA_NOSYSMENU = 0x10;

    public const int WCA_ACCENT_POLICY = 19;
    public const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

    [DllImport("user32.dll")]
    public static extern nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint hModWinEventProc,
        WinEventDelegate lPfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(nint hWinEventHook);

    [DllImport("user32.dll")]
    public static extern nint GetParent(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint FindWindowEx(nint parentHandle, nint childAfter, string className, string? windowTitle);

    [DllImport("user32.dll", ExactSpelling = true, EntryPoint = "MapVirtualKeyW")]
    public static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    public static extern uint SendInput(uint nInputs, [MarshalAs(UnmanagedType.LPArray), In] INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(nint handle, int nCmdShow);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(nint hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    public static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref MARGINS pMarInset);

    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr64(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr64(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(nint hWnd, int nIndex, int dwNewLong);

    [DllImport("uxtheme.dll", ExactSpelling = true)]
    public static extern int SetWindowThemeAttribute(nint hwnd, int eAttribute, ref WTA_OPTIONS pvAttribute, int cbAttribute);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowCompositionAttribute(nint hwnd, ref WINDOWCOMPOSITIONATTRIBDATA data);

    [StructLayout(LayoutKind.Sequential)]
    public struct WTA_OPTIONS
    {
        public uint dwFlags;
        public uint dwMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ACCENT_POLICY
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWCOMPOSITIONATTRIBDATA
    {
        public int Attribute;
        public nint Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    public static extern bool IsIconic(nint handle);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(nint handle);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(nint handle);

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern uint RealGetWindowClass(nint hwnd, StringBuilder pszType, uint cchType);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern nint SendMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [return: MarshalAs(UnmanagedType.Bool)]
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool QueryFullProcessImageName(nint hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("shell32.dll")]
    public static extern int SHGetDesktopFolder(out nint ppshf);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHGetNameFromIDList(nint pidl, uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string? ppszName);

    public static IEnumerable<nint> FindAllWindowsEx(string className, nint parent = 0, string? windowTitle = null)
    {
        nint handle = 0;
        do
        {
            handle = FindWindowEx(parent, handle, className, windowTitle);
            if (handle == 0) continue;
            yield return handle;
        } while (handle != 0);
    }

    public static void RestoreWindowToForeground(nint window)
    {
        if (IsIconic(window))
            ShowWindow(window, SW_SHOWNOACTIVATE);

        if (SetForegroundWindow(window)) return;

        Helper.BypassWinForegroundRestrictions();
        SetForegroundWindow(window);
    }

    public static string GetWindowClassName(nint hWnd, int maxClassNameLength = 254)
    {
        if (hWnd == 0) return string.Empty;

        var className = new StringBuilder(maxClassNameLength + 1);
        RealGetWindowClass(hWnd, className, (uint)className.Capacity);
        return className.ToString();
    }

    public static bool IsWindowHasClassName(nint hWnd, string className, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        var currentClassName = GetWindowClassName(hWnd, className.Length);
        return string.Equals(currentClassName, className, comparison);
    }

    /// <summary>
    /// Extends the DWM glass frame over the whole client area so a non-layered
    /// WPF window can be transparent and let the WCA acrylic backdrop show
    /// through, while DWM still rounds the window corners.
    /// </summary>
    public static void ExtendGlassFrame(nint hwnd)
    {
        if (hwnd == 0) return;

        var margins = new MARGINS
        {
            cxLeftWidth = -1,
            cxRightWidth = -1,
            cyTopHeight = -1,
            cyBottomHeight = -1,
        };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);
    }

    /// <summary>
    /// Asks DWM to round the window corners (only applies to non-layered
    /// windows, which also clips the acrylic backdrop to the rounded shape).
    /// </summary>
    public static void ApplyRoundedCorners(nint hwnd)
    {
        if (hwnd == 0) return;

        var corner = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
    }

    /// <summary>
    /// Classic acrylic via SetWindowCompositionAttribute (works on Windows 10
    /// and 11, layered windows included). Color is ABGR: 0xAABBGGRR.
    /// </summary>
    public static void ApplyLegacyAcrylic(nint hwnd, int gradientColor)
    {
        if (hwnd == 0) return;

        var accent = new ACCENT_POLICY
        {
            AccentState = ACCENT_ENABLE_ACRYLICBLURBEHIND,
            GradientColor = gradientColor,
        };
        var data = new WINDOWCOMPOSITIONATTRIBDATA
        {
            Attribute = WCA_ACCENT_POLICY,
            Data = Marshal.AllocHGlobal(Marshal.SizeOf<ACCENT_POLICY>()),
            SizeOfData = Marshal.SizeOf<ACCENT_POLICY>(),
        };
        try
        {
            Marshal.StructureToPtr(accent, data.Data, false);
            // Returns BOOL: nonzero means success.
            if (SetWindowCompositionAttribute(hwnd, ref data) == 0)
                Log.Warn($"SetWindowCompositionAttribute(acrylic) failed: {Marshal.GetLastWin32Error()}");
        }
        finally
        {
            Marshal.FreeHGlobal(data.Data);
        }
    }

    /// <summary>
    /// Makes DWM treat a visually borderless window as a native frame: keeps
    /// WS_CAPTION so system backdrops (Mica/Acrylic), shadows and rounded
    /// corners actually render, while hiding the drawn caption and system menu.
    /// </summary>
    public static void MakeWindowLookNative(nint hwnd)
    {
        if (hwnd == 0) return;

        var style = IntPtr.Size == 8
            ? GetWindowLongPtr64(hwnd, GWL_STYLE).ToInt64()
            : GetWindowLong32(hwnd, GWL_STYLE);
        style |= WS_CAPTION;
        style &= ~WS_SYSMENU;
        if (IntPtr.Size == 8)
            SetWindowLongPtr64(hwnd, GWL_STYLE, new nint(style));
        else
            SetWindowLong32(hwnd, GWL_STYLE, (int)style);

        var options = new WTA_OPTIONS
        {
            dwFlags = WTNCA_NODRAWCAPTION | WTNCA_NODRAWICON | WTNCA_NOMIRRORHELP | WTNCA_NOSYSMENU,
            dwMask = WTNCA_NODRAWCAPTION | WTNCA_NODRAWICON | WTNCA_NOMIRRORHELP | WTNCA_NOSYSMENU,
        };
        SetWindowThemeAttribute(hwnd, WTA_NONCLIENT, ref options, Marshal.SizeOf<WTA_OPTIONS>());
    }

    /// <summary>
    /// Applies a Windows 11 native system backdrop plus DWM rounded corners and
    /// a dark-mode tint. Requires Windows 11 22H2 (Build 22621) or later; DWM
    /// failures are logged but do not crash the app.
    /// </summary>
    public static void ApplyWin11Backdrop(nint hwnd, Win11Backdrop backdrop)
    {
        if (hwnd == 0) return;

        var darkMode = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

        var backdropValue = backdrop switch
        {
            Win11Backdrop.Acrylic => DWMSBT_TRANSIENTWINDOW,
            Win11Backdrop.MicaAlt => DWMSBT_TABBEDWINDOW,
            _ => DWMSBT_MAINWINDOW,
        };
        var hr = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdropValue, sizeof(int));
        if (hr != 0)
            Log.Warn($"DwmSetWindowAttribute(SYSTEMBACKDROP_TYPE={backdrop}) failed: 0x{hr:X8}");

        var corner = DWMWCP_ROUND;
        hr = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        if (hr != 0)
            Log.Warn($"DwmSetWindowAttribute(WINDOW_CORNER_PREFERENCE) failed: 0x{hr:X8}");
    }


    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(nint hProcess);

    /// <summary>
    /// Hands idle, private working-set pages back to the OS. Called only while
    /// the app is idle; it lowers the Working Set shown in Task Manager without
    /// affecting the app's behavior (pages are re-faulted on next access).
    /// </summary>
    public static void TrimWorkingSet()
    {
        try
        {
            EmptyWorkingSet(GetCurrentProcess());
        }
        catch
        {
            // Best effort: trimming memory must never throw.
        }
    }
    public static string? GetProcessPath(int pid)
    {
        const uint processQueryLimitedInformation = 0x1000;
        var procHandle = OpenProcess(processQueryLimitedInformation, false, (uint)pid);
        if (procHandle == 0) return null;

        try
        {
            var capacity = 260;
            var sb = new StringBuilder(capacity);
            return QueryFullProcessImageName(procHandle, 0, sb, ref capacity) ? sb.ToString() : null;
        }
        finally
        {
            CloseHandle(procHandle);
        }
    }
}
