using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Boxroom.VRScreen.Host;

internal static class Program
{
    [STAThread] private static void Main(string[] args)
    {
        int ownerProcessId = 0;
        if (args.Length >= 2 && args[0].Equals("--boxroom-pid", StringComparison.OrdinalIgnoreCase))
            int.TryParse(args[1], out ownerProcessId);
        Application.EnableVisualStyles();
        using (var context = new CaptureContext(ownerProcessId)) Application.Run(context);
    }
}

internal sealed class CaptureContext : ApplicationContext
{
    private const int HotkeyId = 0x425652;
    private const uint ModNoRepeat = 0x4000;
    private const int GwlStyle = -16, GwlExStyle = -20;
    private const long WsVisible = 0x10000000L;
    private const long WsPopup = 0x80000000L, WsCaption = 0x00C00000L, WsThickFrame = 0x00040000L;
    private const long WsSysMenu = 0x00080000L, WsMinimizeBox = 0x00020000L, WsMaximizeBox = 0x00010000L;
    private const long WsExTopmost = 0x00000008L, WsExTransparent = 0x00000020L, WsExLayered = 0x00080000L;
    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020, SwpShowWindow = 0x0040;
    private const uint Th32csSnapProcess = 0x00000002;
    private static readonly IntPtr HwndTopmost = new IntPtr(-1), HwndNotTopmost = new IntPtr(-2);
    private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);
    private const int Header = 48, MaxWidth = 960, MaxHeight = 540;
    private const int Capacity = Header + 1280 * 720 * 4;
    private readonly string requestPath = Path.Combine(Directory.GetCurrentDirectory(), "launch.request");
    private readonly string logPath = Path.Combine(Directory.GetCurrentDirectory(), "host.log");
    private readonly MemoryMappedFile map = MemoryMappedFile.CreateOrOpen("BoxroomVRScreenFrames_v2", Capacity);
    private readonly MemoryMappedViewAccessor view;
    private readonly System.Windows.Forms.Timer timer;
    private readonly HotkeyWindow hotkey;
    private readonly int ownerProcessId;
    private IntPtr boxroomWindow, gameWindow;
    private int boxroomProcessId;
    private IntPtr originalGameStyle, originalBoxroomExStyle;
    private RECT originalGameRect;
    private bool gameStaged, boxroomOverlayApplied;
    private string expectedProcess = "";
    private int launchedProcessId;
    private long requestStamp;
    private DateTime requestReceivedUtc;
    private readonly HashSet<long> windowsBeforeLaunch = new HashSet<long>();
    private readonly HashSet<int> processesBeforeLaunch = new HashSet<int>();
    private int sequence;
    private int pointerDeltaSequence;
    private float cumulativePointerDeltaX, cumulativePointerDeltaY;
    private POINT lastAppliedCursor;
    private bool cursorReferenceReady;
    private bool tvModeActive, boxroomObservedAlive, hotkeyRegistered;
    private Bitmap sourceBitmap, outputBitmap;
    private Graphics sourceGraphics, outputGraphics;
    private byte[] pixelBuffer;
    private int bufferSourceWidth, bufferSourceHeight, bufferWidth, bufferHeight;

    internal CaptureContext(int ownerProcessId)
    {
        this.ownerProcessId = ownerProcessId;
        view = map.CreateViewAccessor(0, Capacity);
        hotkey = new HotkeyWindow(ToggleTvMode);
        timer = new System.Windows.Forms.Timer { Interval = 8 };
        timer.Tick += (_, __) => Tick();
        timer.Start();
        if (ownerProcessId > 0) Log("Helper attached to BOXROOM process PID " + ownerProcessId + ".");
    }

    private void Tick()
    {
        ReadRequest();
        int state = view.ReadInt32(12);
        if (state == 7)
        {
            Log("BOXROOM requested helper shutdown.");
            EndHostedSession(true);
            view.Write(12, 0);
            ExitThread();
            return;
        }
        if (ownerProcessId > 0 && !IsProcessAlive(ownerProcessId))
        {
            Log("The owning BOXROOM process closed; restoring the staged game window and stopping the helper.");
            EndHostedSession(true);
            ExitThread();
            return;
        }
        RefreshBoxroomWindow();
        bool boxroomAlive = boxroomProcessId > 0
            ? IsProcessAlive(boxroomProcessId)
            : boxroomWindow != IntPtr.Zero && IsWindow(boxroomWindow);
        if (boxroomAlive)
        {
            boxroomObservedAlive = true;
        }
        else if (boxroomWindow != IntPtr.Zero)
        {
            if (!boxroomObservedAlive)
            {
                Log("Ignored a stale launch request from a previous BOXROOM process.");
                EndHostedSession(true);
                boxroomWindow = gameWindow = IntPtr.Zero;
                tvModeActive = false;
                view.Write(12, 0);
                return;
            }

            Log("The current BOXROOM process closed; restoring the staged game window and stopping the helper.");
            EndHostedSession(true);
            ExitThread();
            return;
        }
        if (gameWindow != IntPtr.Zero && !IsWindow(gameWindow))
        {
            EndHostedSession(false);
            gameWindow = IntPtr.Zero;
            tvModeActive = false;
            FocusBoxroom();
            view.Write(12, 4);
            return;
        }

        if (!tvModeActive && state == 1) FindGame();
        else if (!tvModeActive && state == 5) ResumeGameInput();
        else if (tvModeActive && state == 6) ReturnToRoom();
        if (gameStaged) SyncStagedGameBounds();
        if (tvModeActive) ApplyVirtualCursorPosition();
        else if (gameWindow != IntPtr.Zero && IsWindow(gameWindow)) ClipBoxroomCursor();
    }

    private void ReadRequest()
    {
        if (!File.Exists(requestPath)) return;
        long stamp = File.GetLastWriteTimeUtc(requestPath).Ticks;
        if (stamp == requestStamp) return;
        requestStamp = stamp;
        string[] parts;
        try { parts = File.ReadAllText(requestPath).Split('|'); } catch { return; }
        if (parts.Length < 3 || !long.TryParse(parts[0], out long hwnd)) return;
        EndHostedSession(true);
        boxroomWindow = new IntPtr(hwnd);
        boxroomProcessId = 0;
        if (parts.Length >= 5) int.TryParse(parts[4], out boxroomProcessId);
        boxroomObservedAlive = IsWindow(boxroomWindow) || IsProcessAlive(boxroomProcessId);
        expectedProcess = parts[1];
        requestReceivedUtc = DateTime.UtcNow;
        windowsBeforeLaunch.Clear();
        if (parts.Length >= 4)
        {
            foreach (string rawWindow in parts[3].Split(','))
                if (long.TryParse(rawWindow, out long knownWindow)) windowsBeforeLaunch.Add(knownWindow);
        }
        processesBeforeLaunch.Clear();
        if (parts.Length >= 6)
        {
            foreach (string rawProcess in parts[5].Split(','))
                if (int.TryParse(rawProcess, out int knownProcess)) processesBeforeLaunch.Add(knownProcess);
        }
        launchedProcessId = 0;
        cumulativePointerDeltaX = cumulativePointerDeltaY = 0f;
        pointerDeltaSequence = 0;
        view.Write(36, 0f);
        view.Write(40, 0f);
        view.Write(32, 0);
        gameWindow = IntPtr.Zero;
        tvModeActive = false;
        cursorReferenceReady = false;
        view.Write(12, 1);
        Log("Launch request received; expected process=" +
            (string.IsNullOrWhiteSpace(expectedProcess) ? "<automatic>" : expectedProcess) + ".");
    }

    private void RefreshBoxroomWindow()
    {
        if (IsWindow(boxroomWindow) || !IsProcessAlive(boxroomProcessId)) return;
        try
        {
            Process process = Process.GetProcessById(boxroomProcessId);
            process.Refresh();
            IntPtr main = process.MainWindowHandle;
            if (main != IntPtr.Zero && IsWindow(main)) boxroomWindow = main;
        }
        catch { }
    }

    private static bool IsProcessAlive(int processId)
    {
        if (processId <= 0) return false;
        try { return !Process.GetProcessById(processId).HasExited; }
        catch { return false; }
    }

    private void FindGame()
    {
        if (launchedProcessId == 0 && !string.IsNullOrWhiteSpace(expectedProcess))
        {
            foreach (Process process in Process.GetProcessesByName(expectedProcess))
            {
                try
                {
                    if (processesBeforeLaunch.Contains(process.Id) ||
                        process.StartTime.ToUniversalTime() < requestReceivedUtc.AddSeconds(-2)) continue;
                    launchedProcessId = process.Id;
                    break;
                }
                catch { }
            }
        }

        if (launchedProcessId == 0)
        {
            long bestArea = 0;
            EnumWindows((hwnd, _) =>
            {
                if (!IsUsableGameWindow(hwnd, false) || !GetClientRect(hwnd, out RECT rect)) return true;
                GetWindowThreadProcessIdForProcess(hwnd, out uint rawPid);
                int pid = (int)rawPid;
                if (processesBeforeLaunch.Contains(pid)) return true;
                try
                {
                    Process process = Process.GetProcessById(pid);
                    if (!string.IsNullOrWhiteSpace(expectedProcess) &&
                        !process.ProcessName.Equals(expectedProcess, StringComparison.OrdinalIgnoreCase)) return true;
                    if (process.StartTime.ToUniversalTime() < requestReceivedUtc.AddSeconds(-2)) return true;
                }
                catch { return true; }
                long area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
                if (area <= bestArea) return true;
                bestArea = area;
                launchedProcessId = pid;
                return true;
            }, IntPtr.Zero);

            if (launchedProcessId != 0)
                Log("Identified launched game process PID " + launchedProcessId +
                    "; window discovery is restricted to it and verified descendants.");
        }

        if (launchedProcessId == 0) return;

        Dictionary<int, int> parents = SnapshotProcessParents();
        IntPtr found = IntPtr.Zero;
        long largestArea = 0;
        EnumWindows((hwnd, _) =>
        {
            if (!IsUsableGameWindow(hwnd, false) || !GetClientRect(hwnd, out RECT rect)) return true;
            GetWindowThreadProcessIdForProcess(hwnd, out uint rawPid);
            int pid = (int)rawPid;
            if (pid != launchedProcessId && !IsDescendantOf(pid, launchedProcessId, parents)) return true;
            long area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
            if (area <= largestArea) return true;
            largestArea = area;
            found = hwnd;
            return true;
        }, IntPtr.Zero);

        if (found == IntPtr.Zero) return;
        gameWindow = found;
        tvModeActive = true;
        RegisterSessionHotkey();
        bool staged = TryStageGameWindow();
        view.Write(16, gameWindow.ToInt64());
        view.Write(12, 2);
        if (staged) EnterBoxroomOverlay();
        else FocusWindow(gameWindow);
        Log("Captured game window: " + DescribeWindow(gameWindow) + ".");
        Log(staged
            ? "Game is staged borderless directly behind BOXROOM; keyboard and click-through mouse input go to the real game window."
            : "Single-monitor staging was unavailable; captured game owns normal keyboard/mouse focus and BOXROOM mirrors its cursor onto the TV reticle.");
    }

    private static Dictionary<int, int> SnapshotProcessParents()
    {
        var result = new Dictionary<int, int>();
        IntPtr snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == InvalidHandleValue) return result;
        try
        {
            PROCESSENTRY32 entry = new PROCESSENTRY32 { Size = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32)) };
            if (!Process32First(snapshot, ref entry)) return result;
            do { result[(int)entry.ProcessId] = (int)entry.ParentProcessId; }
            while (Process32Next(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
        return result;
    }

    private static bool IsDescendantOf(int processId, int ancestorId, Dictionary<int, int> parents)
    {
        var visited = new HashSet<int>();
        int current = processId;
        while (current > 0 && visited.Add(current) && parents.TryGetValue(current, out int parent))
        {
            if (parent == ancestorId) return true;
            current = parent;
        }
        return false;
    }

    private bool IsUsableGameWindow(IntPtr hwnd, bool requireNewProcess)
    {
        if (hwnd == IntPtr.Zero || hwnd == boxroomWindow || !IsWindow(hwnd) || !IsWindowVisible(hwnd) ||
            GetWindowTextLength(hwnd) == 0 || !GetClientRect(hwnd, out RECT rect) ||
            rect.Right - rect.Left < 320 || rect.Bottom - rect.Top < 200)
            return false;

        GetWindowThreadProcessIdForProcess(hwnd, out uint pid);
        try
        {
            Process process = Process.GetProcessById((int)pid);
            string name = process.ProcessName;
            if (name.IndexOf("boxroom", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.Equals("explorer", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("steam", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("steamwebhelper", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("startmenuexperiencehost", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("searchhost", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("searchapp", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("shellexperiencehost", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("textinputhost", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("applicationframehost", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("systemsettings", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("taskmgr", StringComparison.OrdinalIgnoreCase))
                return false;
            if (name.Equals("dwm", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("discord", StringComparison.OrdinalIgnoreCase) ||
                name.IndexOf("overlay", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            return !requireNewProcess || process.StartTime.ToUniversalTime() >= requestReceivedUtc.AddSeconds(-2);
        }
        catch { return false; }
    }

    private static string DescribeWindow(IntPtr hwnd)
    {
        GetWindowThreadProcessIdForProcess(hwnd, out uint pid);
        string processName;
        try { processName = Process.GetProcessById((int)pid).ProcessName; }
        catch { processName = "unknown"; }
        var title = new StringBuilder(Math.Max(1, GetWindowTextLength(hwnd) + 1));
        GetWindowText(hwnd, title, title.Capacity);
        return processName + " (" + title + ", hwnd=" + hwnd.ToInt64() + ")";
    }

    private void Log(string message)
    {
        try { File.AppendAllText(logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message + Environment.NewLine); }
        catch { }
    }

    private void Capture()
    {
        if (!GetClientRect(gameWindow, out RECT rect)) return;
        POINT origin = new POINT();
        if (!ClientToScreen(gameWindow, ref origin)) return;
        int sourceWidth = rect.Right - rect.Left, sourceHeight = rect.Bottom - rect.Top;
        if (sourceWidth < 16 || sourceHeight < 16) return;
        float scale = Math.Min(1f, Math.Min((float)MaxWidth / sourceWidth, (float)MaxHeight / sourceHeight));
        int width = Math.Max(1, (int)(sourceWidth * scale)), height = Math.Max(1, (int)(sourceHeight * scale));
        try
        {
            EnsureBuffers(sourceWidth, sourceHeight, width, height);
            sourceGraphics.CopyFromScreen(origin.X, origin.Y, 0, 0, new Size(sourceWidth, sourceHeight), CopyPixelOperation.SourceCopy);
            outputGraphics.DrawImage(sourceBitmap, new Rectangle(0, 0, width, height));
            BitmapData data = outputBitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                Marshal.Copy(data.Scan0, pixelBuffer, 0, pixelBuffer.Length);
                for (int i = 3; i < pixelBuffer.Length; i += 4) pixelBuffer[i] = 255;
                view.WriteArray(Header, pixelBuffer, 0, pixelBuffer.Length);
                view.Write(4, width); view.Write(8, height); view.Write(0, ++sequence);
            }
            finally { outputBitmap.UnlockBits(data); }
        }
        catch { }
    }

    private void EnsureBuffers(int sourceWidth, int sourceHeight, int width, int height)
    {
        if (sourceBitmap != null && bufferSourceWidth == sourceWidth && bufferSourceHeight == sourceHeight && bufferWidth == width && bufferHeight == height) return;
        sourceGraphics?.Dispose(); outputGraphics?.Dispose(); sourceBitmap?.Dispose(); outputBitmap?.Dispose();
        sourceBitmap = new Bitmap(sourceWidth, sourceHeight, PixelFormat.Format32bppArgb);
        outputBitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        sourceGraphics = Graphics.FromImage(sourceBitmap);
        outputGraphics = Graphics.FromImage(outputBitmap);
        outputGraphics.CompositingMode = CompositingMode.SourceCopy;
        outputGraphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        outputGraphics.PixelOffsetMode = PixelOffsetMode.Half;
        pixelBuffer = new byte[width * height * 4];
        bufferSourceWidth = sourceWidth; bufferSourceHeight = sourceHeight; bufferWidth = width; bufferHeight = height;
    }

    private void ToggleTvMode()
    {
        if (gameWindow == IntPtr.Zero || !IsWindow(gameWindow))
        {
            Log("F2 ignored because no game window is active.");
            return;
        }
        if (tvModeActive) ReturnToRoom();
    }

    private void ResumeGameInput()
    {
        if (gameWindow == IntPtr.Zero || !IsWindow(gameWindow)) return;
        tvModeActive = true;
        cursorReferenceReady = false;
        RegisterSessionHotkey();
        if (gameStaged) EnterBoxroomOverlay();
        else FocusWindow(gameWindow);
        view.Write(12, 2);
        Log("Boxroom-TV focused this VRScreen display; external game input resumed.");
    }

    private void ReturnToRoom()
    {
        tvModeActive = false;
        cursorReferenceReady = false;
        ClipBoxroomCursor();
        UnregisterSessionHotkey();
        ExitBoxroomOverlay();
        FocusBoxroom();
        ClipBoxroomCursor();
        view.Write(12, 3);
        Log("F2 returned the active VRScreen display to BOXROOM.");
    }

    private void FocusBoxroom()
    {
        FocusWindow(boxroomWindow);
    }

    private bool TryStageGameWindow()
    {
        if (!GetWindowRect(gameWindow, out originalGameRect) || !GetWindowRect(boxroomWindow, out RECT boxroomRect))
            return false;

        try
        {
            originalGameStyle = GetWindowLongPtr(gameWindow, GwlStyle);
            long stagedStyle = IntPtr.Size == 8
                ? originalGameStyle.ToInt64()
                : unchecked((uint)originalGameStyle.ToInt32());
            stagedStyle &= ~(WsCaption | WsThickFrame | WsSysMenu | WsMinimizeBox | WsMaximizeBox);
            stagedStyle |= WsPopup | WsVisible;
            SetWindowLongPtr(gameWindow, GwlStyle, new IntPtr(stagedStyle));

            gameStaged = true;
            PositionStagedGame(boxroomRect);
            Log("Prepared a single-monitor native game surface behind BOXROOM at " +
                (boxroomRect.Right - boxroomRect.Left) + "x" + (boxroomRect.Bottom - boxroomRect.Top) + ".");
            return true;
        }
        catch (Exception ex)
        {
            Log("Single-monitor game staging failed; using ordinary window focus instead: " + ex.Message);
            RestoreStagedGame();
            return false;
        }
    }

    private void SyncStagedGameBounds()
    {
        if (!gameStaged || !IsWindow(gameWindow) || !IsWindow(boxroomWindow) ||
            !GetWindowRect(boxroomWindow, out RECT bounds)) return;
        PositionStagedGame(bounds);
    }

    private void PositionStagedGame(RECT bounds)
    {
        SetWindowPos(gameWindow, boxroomWindow, bounds.Left, bounds.Top,
            Math.Max(1, bounds.Right - bounds.Left), Math.Max(1, bounds.Bottom - bounds.Top),
            SwpNoActivate | SwpFrameChanged | SwpShowWindow);
        if (tvModeActive) ClipGameCursor();
    }

    private void ClipGameCursor()
    {
        ClipCursorToClient(gameWindow);
    }

    private void ClipBoxroomCursor()
    {
        ClipCursorToClient(boxroomWindow);
    }

    private static void ClipCursorToClient(IntPtr window)
    {
        if (window == IntPtr.Zero || !IsWindow(window) || !GetClientRect(window, out RECT client)) return;
        POINT topLeft = new POINT { X = client.Left, Y = client.Top };
        POINT bottomRight = new POINT { X = client.Right, Y = client.Bottom };
        if (!ClientToScreen(window, ref topLeft) || !ClientToScreen(window, ref bottomRight)) return;
        RECT clip = new RECT
        {
            Left = topLeft.X,
            Top = topLeft.Y,
            Right = bottomRight.X,
            Bottom = bottomRight.Y
        };
        ClipCursor(ref clip);
    }

    private void EnterBoxroomOverlay()
    {
        if (!gameStaged || !IsWindow(boxroomWindow))
        {
            FocusWindow(gameWindow);
            return;
        }

        if (!boxroomOverlayApplied)
        {
            originalBoxroomExStyle = GetWindowLongPtr(boxroomWindow, GwlExStyle);
            SetWindowLongPtr(boxroomWindow, GwlExStyle,
                new IntPtr(originalBoxroomExStyle.ToInt64() | WsExLayered | WsExTransparent));
            boxroomOverlayApplied = true;
        }

        SetWindowPos(boxroomWindow, HwndTopmost, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpFrameChanged | SwpShowWindow);
        FocusWindow(gameWindow);
    }

    private void ExitBoxroomOverlay()
    {
        if (!boxroomOverlayApplied) return;
        if (IsWindow(boxroomWindow))
        {
            SetWindowLongPtr(boxroomWindow, GwlExStyle, originalBoxroomExStyle);
            IntPtr restoredZOrder = (originalBoxroomExStyle.ToInt64() & WsExTopmost) != 0
                ? HwndTopmost
                : HwndNotTopmost;
            SetWindowPos(boxroomWindow, restoredZOrder, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoActivate | SwpFrameChanged | SwpShowWindow);
        }
        boxroomOverlayApplied = false;
        originalBoxroomExStyle = IntPtr.Zero;
    }

    private void EndHostedSession(bool restoreGame)
    {
        cursorReferenceReady = false;
        ClipCursor(IntPtr.Zero);
        UnregisterSessionHotkey();
        ExitBoxroomOverlay();
        if (gameStaged && restoreGame && IsWindow(gameWindow))
        {
            RestoreStagedGame();
            Log("Restored the game window to its original style and bounds.");
        }
        gameStaged = false;
        originalGameStyle = IntPtr.Zero;
    }

    private void RegisterSessionHotkey()
    {
        if (hotkeyRegistered) return;
        hotkeyRegistered = RegisterHotKey(hotkey.Handle, HotkeyId, ModNoRepeat, 0x71);
        if (!hotkeyRegistered) Log("Could not reserve F2 for the active external-game session.");
    }

    private void UnregisterSessionHotkey()
    {
        if (!hotkeyRegistered) return;
        UnregisterHotKey(hotkey.Handle, HotkeyId);
        hotkeyRegistered = false;
    }

    private void RestoreStagedGame()
    {
        if (!IsWindow(gameWindow) || originalGameStyle == IntPtr.Zero) return;
        SetWindowLongPtr(gameWindow, GwlStyle, originalGameStyle);
        SetWindowPos(gameWindow, IntPtr.Zero, originalGameRect.Left, originalGameRect.Top,
            Math.Max(1, originalGameRect.Right - originalGameRect.Left),
            Math.Max(1, originalGameRect.Bottom - originalGameRect.Top),
            SwpFrameChanged | SwpShowWindow);
    }

    private void ApplyVirtualCursorPosition()
    {
        if (!GetClientRect(gameWindow, out RECT client) || client.Right <= 1 || client.Bottom <= 1) return;
        if (GetCursorPos(out POINT current) && cursorReferenceReady)
        {
            float deltaX = (current.X - lastAppliedCursor.X) / (float)(client.Right - 1);
            float deltaY = -(current.Y - lastAppliedCursor.Y) / (float)(client.Bottom - 1);
            if (Math.Abs(deltaX) > 0.000001f || Math.Abs(deltaY) > 0.000001f)
            {
                cumulativePointerDeltaX += deltaX;
                cumulativePointerDeltaY += deltaY;
                view.Write(36, cumulativePointerDeltaX);
                view.Write(40, cumulativePointerDeltaY);
                view.Write(32, ++pointerDeltaSequence);
            }
        }
        float x = Math.Max(0f, Math.Min(1f, view.ReadSingle(24)));
        float y = Math.Max(0f, Math.Min(1f, view.ReadSingle(28)));
        POINT point = new POINT
        {
            X = (int)Math.Round(x * (client.Right - 1)),
            Y = (int)Math.Round((1f - y) * (client.Bottom - 1))
        };
        if (!ClientToScreen(gameWindow, ref point)) return;
        SetCursorPos(point.X, point.Y);
        lastAppliedCursor = point;
        cursorReferenceReady = true;
    }

    private void FocusWindow(IntPtr targetWindow)
    {
        if (targetWindow == IntPtr.Zero || !IsWindow(targetWindow)) return;
        IntPtr foreground = GetForegroundWindow();
        uint currentThread = GetCurrentThreadId();
        uint foregroundThread = foreground != IntPtr.Zero ? GetWindowThreadProcessId(foreground, IntPtr.Zero) : 0;
        uint targetThread = GetWindowThreadProcessId(targetWindow, IntPtr.Zero);
        bool joinedForeground = foregroundThread != 0 && foregroundThread != currentThread && AttachThreadInput(currentThread, foregroundThread, true);
        bool joinedTarget = targetThread != 0 && targetThread != currentThread && AttachThreadInput(currentThread, targetThread, true);
        try
        {
            ShowWindow(targetWindow, 9);
            BringWindowToTop(targetWindow);
            SetForegroundWindow(targetWindow);
            SetFocus(targetWindow);
        }
        finally
        {
            if (joinedTarget) AttachThreadInput(currentThread, targetThread, false);
            if (joinedForeground) AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            EndHostedSession(true);
            timer.Dispose(); hotkey.DestroyHandle();
            sourceGraphics?.Dispose(); outputGraphics?.Dispose(); sourceBitmap?.Dispose(); outputBitmap?.Dispose();
            view.Dispose(); map.Dispose();
        }
        base.Dispose(disposing);
    }


    private sealed class HotkeyWindow : NativeWindow
    {
        private readonly Action pressed;
        internal HotkeyWindow(Action pressed) { this.pressed = pressed; CreateHandle(new CreateParams()); }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0312 && message.WParam.ToInt32() == HotkeyId) pressed();
            base.WndProc(ref message);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PROCESSENTRY32
    {
        internal uint Size;
        internal uint Usage;
        internal uint ProcessId;
        internal IntPtr DefaultHeapId;
        internal uint ModuleId;
        internal uint Threads;
        internal uint ParentProcessId;
        internal int BasePriority;
        internal uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string ExeFile;
    }
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr processId);
    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    private static extern uint GetWindowThreadProcessIdForProcess(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool value);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)] private static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)] private static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hwnd, ref POINT point);
    [DllImport("user32.dll", EntryPoint = "ClipCursor")] private static extern bool ClipCursor(ref RECT rect);
    [DllImport("user32.dll", EntryPoint = "ClipCursor")] private static extern bool ClipCursor(IntPtr rect);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr32(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr32(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : GetWindowLongPtr32(hwnd, index);

    private static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(hwnd, index, value) : SetWindowLongPtr32(hwnd, index, value);
}
