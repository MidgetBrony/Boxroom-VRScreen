using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Boxroom_VRScreen;

internal static class NativeWindowCapture
{
    internal const int MaxWidth = 1280;
    internal const int MaxHeight = 720;
    private static readonly HashSet<string> RejectedProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "steam", "steamwebhelper", "startmenuexperiencehost", "searchhost", "searchapp",
        "shellexperiencehost", "textinputhost", "applicationframehost", "systemsettings", "taskmgr",
        "dwm", "discord"
    };

    internal static IntPtr ForegroundWindow => GetForegroundWindow();

    internal static bool IsUsableGameWindow(IntPtr hwnd, IntPtr boxroomWindow)
    {
        if (hwnd == IntPtr.Zero || hwnd == boxroomWindow || !IsWindow(hwnd) || !IsWindowVisible(hwnd) || GetWindowTextLength(hwnd) == 0)
            return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        try
        {
            string name = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant();
            return !RejectedProcesses.Contains(name) && !name.Contains("boxroom");
        }
        catch { return false; }
    }

    internal static HashSet<IntPtr> SnapshotVisibleWindows()
    {
        var result = new HashSet<IntPtr>();
        EnumWindows((hwnd, _) => { if (IsWindowVisible(hwnd)) result.Add(hwnd); return true; }, IntPtr.Zero);
        return result;
    }

    internal static string ExpectedProcessName(string launchPath)
    {
        if (string.IsNullOrWhiteSpace(launchPath) || launchPath.Contains("://")) return string.Empty;
        try { return Path.GetFileNameWithoutExtension(launchPath.Trim().Trim('"')); }
        catch { return string.Empty; }
    }

    internal static IntPtr FindGameWindow(IntPtr boxroomWindow, string expectedProcess, HashSet<IntPtr> windowsBeforeLaunch)
    {
        IntPtr foreground = GetForegroundWindow();
        if (Matches(foreground, boxroomWindow, expectedProcess, windowsBeforeLaunch)) return foreground;
        IntPtr found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            if (!Matches(hwnd, boxroomWindow, expectedProcess, windowsBeforeLaunch)) return true;
            found = hwnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private static bool Matches(IntPtr hwnd, IntPtr boxroomWindow, string expectedProcess, HashSet<IntPtr> windowsBeforeLaunch)
    {
        if (!IsUsableGameWindow(hwnd, boxroomWindow) || !GetClientRect(hwnd, out RECT rect) || rect.Right - rect.Left < 320 || rect.Bottom - rect.Top < 200)
            return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        try
        {
            string actual = Process.GetProcessById((int)pid).ProcessName;
            if (!string.IsNullOrEmpty(expectedProcess)) return actual.Equals(expectedProcess, StringComparison.OrdinalIgnoreCase);
            return windowsBeforeLaunch == null || !windowsBeforeLaunch.Contains(hwnd);
        }
        catch { return false; }
    }

    internal static bool F2Pressed()
    {
        return (GetAsyncKeyState(0x71) & 1) != 0;
    }

    internal static bool F2Down()
    {
        return (GetAsyncKeyState(0x71) & 0x8000) != 0;
    }

    internal static void Focus(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;
        ShowWindow(hwnd, 9);
        SetForegroundWindow(hwnd);
    }

    internal static bool TryCapture(IntPtr hwnd, out byte[] pixels, out int width, out int height)
    {
        pixels = null;
        width = height = 0;
        if (!GetClientRect(hwnd, out RECT rect)) return false;
        int sourceWidth = rect.Right - rect.Left;
        int sourceHeight = rect.Bottom - rect.Top;
        if (sourceWidth < 16 || sourceHeight < 16) return false;

        float scale = Math.Min(1f, Math.Min((float)MaxWidth / sourceWidth, (float)MaxHeight / sourceHeight));
        width = Math.Max(1, (int)(sourceWidth * scale));
        height = Math.Max(1, (int)(sourceHeight * scale));

        POINT origin = default;
        if (!ClientToScreen(hwnd, ref origin)) return false;
        IntPtr desktopDc = GetDC(IntPtr.Zero);
        IntPtr targetDc = CreateCompatibleDC(desktopDc);
        IntPtr bits;
        BITMAPINFO info = new BITMAPINFO
        {
            Header = new BITMAPINFOHEADER
            {
                Size = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER)),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
                Compression = 0
            }
        };
        IntPtr targetBitmap = CreateDIBSection(targetDc, ref info, 0, out bits, IntPtr.Zero, 0);
        IntPtr targetOld = SelectObject(targetDc, targetBitmap);

        try
        {
            SetStretchBltMode(targetDc, 4);
            if (!StretchBlt(targetDc, 0, 0, width, height, desktopDc, origin.X, origin.Y, sourceWidth, sourceHeight, 0x00CC0020)) return false;
            pixels = new byte[width * height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            for (int index = 3; index < pixels.Length; index += 4)
                pixels[index] = 255;
            return true;
        }
        finally
        {
            SelectObject(targetDc, targetOld);
            DeleteObject(targetBitmap);
            DeleteDC(targetDc);
            ReleaseDC(IntPtr.Zero, desktopDc);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct BITMAPINFOHEADER
    {
        internal uint Size; internal int Width; internal int Height; internal ushort Planes; internal ushort BitCount;
        internal uint Compression; internal uint SizeImage; internal int XPelsPerMeter; internal int YPelsPerMeter;
        internal uint ClrUsed; internal uint ClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BITMAPINFO { internal BITMAPINFOHEADER Header; internal uint Colors; }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, uint operation);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr dc, int mode);
}
