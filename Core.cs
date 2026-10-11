using MelonLoader;
using Boxroom_TV;
using SteamShelf;
using SteamShelf.Input;
using SteamShelf.Placeables;
using SteamShelf.PlayerTools;
using SteamShelf.UI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

[assembly: MelonInfo(typeof(Boxroom_VRScreen.Core), "Boxroom-VRScreen", "0.3.35", "MidgetBrony")]
[assembly: MelonGame("NestedLoop", "BOXROOM")]
[assembly: MelonAdditionalDependencies("Boxroom_TV")]

namespace Boxroom_VRScreen;

public sealed class Core : MelonMod
{
    private PlayerInteractionTool interaction;
    private bool primaryWasPressed, waiting, focused, nativeStarted, textureAttached;
    private int lastNativeError;
    private BoxroomTvDisplayLease displayLease;
    private Texture2D externalTexture;
    private Camera camera;
    private Vector3 cameraPosition;
    private Quaternion cameraRotation;
    private Bounds displayBounds;
    private Vector3 displayNormal;
    private FirstPersonController playerController;
    private bool previousPlayerCanMove, previousCameraCanMove;
    private MemoryMappedFile map;
    private MemoryMappedViewAccessor view;
    private string dataRoot;
    private MethodInfo libretroCanHandleGame;
    private bool searchedForLibretroApi;
    private IntPtr gameWindow;
    private readonly List<ReticleState> reticles = new();
    private readonly List<InputActionMap> lockedActionMaps = new();
    private readonly List<PlayerInput> lockedPlayerInputs = new();
    private Vector2 pointerPosition = new(0.5f, 0.5f);
    private int lastPointerDeltaSequence;
    private Vector2 lastPointerDeltaTotal;
    private Process hostProcess;

    private sealed class ReticleState
    {
        internal RectTransform Transform;
        internal Vector2 OriginalPosition;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("VRScreenCapture", CallingConvention = CallingConvention.Cdecl)] private static extern void VRScreen_StartCapture(IntPtr hwnd);
    [DllImport("VRScreenCapture", CallingConvention = CallingConvention.Cdecl)] private static extern void VRScreen_StopCapture();
    [DllImport("VRScreenCapture", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr VRScreen_GetTexture();
    [DllImport("VRScreenCapture", CallingConvention = CallingConvention.Cdecl)] private static extern int VRScreen_GetWidth();
    [DllImport("VRScreenCapture", CallingConvention = CallingConvention.Cdecl)] private static extern int VRScreen_GetHeight();
    [DllImport("VRScreenCapture", CallingConvention = CallingConvention.Cdecl)] private static extern int VRScreen_GetStatus();
    [DllImport("VRScreenCapture", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr VRScreen_GetRenderEventFunc();

    public override void OnInitializeMelon()
    {
        Application.runInBackground = true;
        dataRoot = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "UserData", "Boxroom-VRScreen");
        Directory.CreateDirectory(dataRoot);
        try { File.Delete(Path.Combine(dataRoot, "launch.request")); } catch { }
        StartHost();
        try
        {
            int nativeStatus = VRScreen_GetStatus();
            LoggerInstance.Msg($"GPU capture plugin loaded (status {nativeStatus}). Hold a game box and interact with a television or monitor. F2 returns to BOXROOM.");
        }
        catch (Exception exception)
        {
            LoggerInstance.Error("GPU capture plugin could not load: " + exception);
        }
    }

    public override void OnUpdate()
    {
        interaction ??= UnityEngine.Object.FindFirstObjectByType<PlayerInteractionTool>();
        PlayerInputContext input = Singleton<InputManager>.Instance?.CurrentPlayerInputContext;
        bool pressed = input != null && input.PrimaryPressedThisFrame;
        if (pressed && !primaryWasPressed && !focused && !waiting) TryLaunch();
        primaryWasPressed = pressed;

        EnsureMap();
        int hostState = ReadInt(12);
        if (hostState == Protocol.Closed) { EndSession(); return; }
        if (hostState == Protocol.Playing && !nativeStarted)
        {
            long rawWindow = ReadLong(16);
            if (rawWindow != 0)
            {
                gameWindow = new IntPtr(rawWindow);
                VRScreen_StartCapture(gameWindow);
                nativeStarted = true;
                waiting = false;
                focused = true;
                LockBoxroomInput();
                LoggerInstance.Msg("Game window handed to Windows Graphics Capture.");
            }
        }
        else if (hostState == Protocol.Playing && nativeStarted && !focused)
        {
            ResumeTvMode();
        }
        if (hostState == Protocol.Returned) { ReturnToBoxroom(); return; }
        if (focused && camera != null && displayLease?.IsActive == true)
        {
            KeepBoxroomInputLocked();
            UpdateVirtualPointer();
            if (displayLease.TryGetPointerWorldPosition(pointerPosition, out Vector3 worldPointer))
            {
                Vector3 screenPoint = camera.WorldToScreenPoint(worldPointer);
                Vector2 reticlePosition = new(
                    Mathf.InverseLerp(camera.pixelRect.xMin, camera.pixelRect.xMax, screenPoint.x),
                    Mathf.InverseLerp(camera.pixelRect.yMin, camera.pixelRect.yMax, screenPoint.y));
                UpdatePointerVisual(reticlePosition);
            }
        }
        if (!waiting && !focused) return;
        if (!nativeStarted) return;

        GL.IssuePluginEvent(VRScreen_GetRenderEventFunc(), 1);
        int status = VRScreen_GetStatus();
        if (!textureAttached && status == 2) AttachExternalTexture();
        else if (status < 0 && status != lastNativeError)
        {
            lastNativeError = status;
            LoggerInstance.Error($"Window capture failed (status 0x{unchecked((uint)status):X8}).");
        }
    }

    private void TryLaunch()
    {
        SteamGameData game = interaction?.CurrentHeldSteamGameData;
        if (game == null || IsHandledByLibretro(game)) return;
        if (!TryGetLookedAtDisplay(out PlacementTag tag, out GameImagePainter painter)) return;
        if (!BoxroomTvApi.TryClaimDisplay(painter, tag,
                "com.midgetbrony.boxroom-vrscreen", "Boxroom-VRScreen", out displayLease))
        {
            LoggerInstance.Warning($"Cannot start '{game.Name}': the TV is unsupported or already in use.");
            return;
        }
        displayLease.RegisterFocusHandlers(
            () => focused,
            RequestTvFocus,
            RequestRoomFocus);
        camera = Camera.main;
        if (camera == null) { ReleaseDisplay(); return; }
        cameraPosition = camera.transform.position;
        cameraRotation = camera.transform.rotation;
        playerController = UnityEngine.Object.FindFirstObjectByType<FirstPersonController>();
        if (playerController != null)
        {
            previousPlayerCanMove = playerController.playerCanMove;
            previousCameraCanMove = playerController.cameraCanMove;
            playerController.playerCanMove = false;
            playerController.cameraCanMove = false;
        }
        displayBounds = displayLease.ScreenBounds;
        displayNormal = displayLease.ScreenForward;
        FocusCamera(displayBounds, displayNormal);
        EnsureMap();
        ResetPointerTransport();
        BeginPointer();
        string expected = NativeWindowCapture.ExpectedProcessName(game.LaunchExePath);
        HashSet<IntPtr> windowsBeforeLaunch = NativeWindowCapture.SnapshotVisibleWindows();
        string windowSnapshot = string.Join(",", windowsBeforeLaunch.Select(window => window.ToInt64().ToString()));
        string processSnapshot = string.Join(",", Process.GetProcesses().Select(process => process.Id.ToString()));
        Process boxroomProcess = Process.GetCurrentProcess();
        boxroomProcess.Refresh();
        IntPtr boxroomWindow = boxroomProcess.MainWindowHandle;
        if (boxroomWindow == IntPtr.Zero) boxroomWindow = GetForegroundWindow();
        File.WriteAllText(Path.Combine(dataRoot, "launch.request"),
            $"{boxroomWindow.ToInt64()}|{expected}|{DateTime.UtcNow.Ticks}|{windowSnapshot}|{boxroomProcess.Id}|{processSnapshot}");
        waiting = true; focused = nativeStarted = textureAttached = false;
        lastNativeError = 0;
        SteamLibrarySystem.LaunchGame(game);
        LoggerInstance.Msg($"Launching {game.Name} on VRScreen via {(string.IsNullOrWhiteSpace(expected) ? "automatic window discovery" : expected)}. Press F2 to return.");
    }

    private bool TryGetLookedAtDisplay(out PlacementTag tag, out GameImagePainter painter)
    {
        tag = interaction?.LookingAtPlaceableTag;
        painter = tag?.GetComponent<GameImagePainter>()
                  ?? tag?.GetComponentInChildren<GameImagePainter>(true);

        if (painter == null)
        {
            Camera viewCamera = Camera.main;
            if (viewCamera == null || !Physics.Raycast(viewCamera.transform.position, viewCamera.transform.forward, out RaycastHit hit, 4f))
            {
                painter = null;
                return false;
            }

            painter = hit.collider.GetComponent<GameImagePainter>()
                      ?? hit.collider.GetComponentInParent<GameImagePainter>();
            tag = hit.collider.GetComponentInParent<PlacementTag>()
                  ?? painter?.GetComponentInParent<PlacementTag>();
            painter ??= tag?.GetComponentInChildren<GameImagePainter>(true);
        }

        return painter != null && tag != null;
    }

    private bool IsHandledByLibretro(SteamGameData game)
    {
        if (!searchedForLibretroApi)
        {
            searchedForLibretroApi = true;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!string.Equals(assembly.GetName().Name, "BR_Libretro", StringComparison.OrdinalIgnoreCase)) continue;
                libretroCanHandleGame = assembly.GetType("BR_Libretro.Core")?.GetMethod(
                    "CanHandleGame", BindingFlags.Public | BindingFlags.Static);
                break;
            }
        }

        if (libretroCanHandleGame == null) return false;
        try { return libretroCanHandleGame.Invoke(null, new object[] { game }) is bool handled && handled; }
        catch (Exception exception)
        {
            LoggerInstance.Warning("Could not query BR-Libretro launch routing: " + exception.Message);
            return false;
        }
    }

    private void FocusCamera(Bounds bounds, Vector3 normal)
    {
        float distance = Mathf.Max(1.15f, bounds.extents.magnitude * 2.1f);
        Vector3 a = bounds.center - normal * distance, b = bounds.center + normal * distance;
        Vector3 position = Vector3.Distance(a, cameraPosition) <= Vector3.Distance(b, cameraPosition) ? a : b;
        camera.transform.position = position;
        camera.transform.rotation = Quaternion.LookRotation(bounds.center - position, Vector3.up);
    }

    private void ResumeTvMode()
    {
        try
        {
            camera = Camera.main;
            if (camera == null) { try { view?.Write(12, 0); } catch { } return; }
            cameraPosition = camera.transform.position;
            cameraRotation = camera.transform.rotation;
            playerController = UnityEngine.Object.FindFirstObjectByType<FirstPersonController>();
            if (playerController != null)
            {
                previousPlayerCanMove = playerController.playerCanMove;
                previousCameraCanMove = playerController.cameraCanMove;
                playerController.playerCanMove = false;
                playerController.cameraCanMove = false;
            }
            FocusCamera(displayBounds, displayNormal);
            BeginPointer();
            LockBoxroomInput();
            focused = true;
            LoggerInstance.Msg("Resuming TV focus and bringing the running game forward. Press F2 to return.");
        }
        catch (Exception exception)
        {
            if (playerController != null)
            {
                playerController.playerCanMove = previousPlayerCanMove;
                playerController.cameraCanMove = previousCameraCanMove;
            }
            focused = false;
            try { view?.Write(12, 0); } catch { }
            LoggerInstance.Error("Could not resume VRScreen focus: " + exception.Message);
        }
    }

    private void RequestTvFocus()
    {
        if (!nativeStarted || gameWindow == IntPtr.Zero || displayLease?.IsActive != true) return;
        try { view?.Write(12, Protocol.ResumeRequested); }
        catch (Exception exception) { LoggerInstance.Warning("Could not request VRScreen focus: " + exception.Message); }
    }

    private void RequestRoomFocus()
    {
        if (!focused) return;
        try { view?.Write(12, Protocol.ReturnRequested); }
        catch (Exception exception) { LoggerInstance.Warning("Could not return VRScreen focus: " + exception.Message); }
    }

    private void AttachExternalTexture()
    {
        int width = VRScreen_GetWidth(), height = VRScreen_GetHeight();
        IntPtr texture = VRScreen_GetTexture();
        if (width <= 0 || height <= 0 || texture == IntPtr.Zero) return;
        externalTexture = Texture2D.CreateExternalTexture(width, height, TextureFormat.BGRA32, false, false, texture);
        externalTexture.wrapMode = TextureWrapMode.Clamp;
        externalTexture.filterMode = FilterMode.Bilinear;
        if (displayLease?.SetTexture(externalTexture, true) != true)
        {
            LoggerInstance.Error("Boxroom-TV rejected the captured texture because the display lease is no longer active.");
            EndSession();
            return;
        }
        textureAttached = true;
        LoggerInstance.Msg($"First captured GPU frame attached through Boxroom-TV ({width}x{height}).");
    }

    private void EnsureMap()
    {
        if (view != null) return;
        try { map = MemoryMappedFile.OpenExisting(Protocol.MapName); view = map.CreateViewAccessor(0, Protocol.Capacity); } catch { }
    }
    private int ReadInt(long offset) { try { return view?.ReadInt32(offset) ?? 0; } catch { return 0; } }
    private long ReadLong(long offset) { try { return view?.ReadInt64(offset) ?? 0; } catch { return 0; } }
    private float ReadFloat(long offset) { try { return view?.ReadSingle(offset) ?? 0f; } catch { return 0f; } }
    private void ReturnToBoxroom()
    {
        RestorePointer();
        UnlockBoxroomInput();
        waiting = focused = false;
        if (camera != null) { camera.transform.position = cameraPosition; camera.transform.rotation = cameraRotation; }
        if (playerController != null)
        {
            playerController.playerCanMove = previousPlayerCanMove;
            playerController.cameraCanMove = previousCameraCanMove;
        }
        Input.ResetInputAxes();
        try { view?.Write(12, 0); } catch { }
        LoggerInstance.Msg("Returned to BOXROOM; the game and last GPU frame remain on the screen.");
    }

    private void EndSession()
    {
        RestorePointer();
        UnlockBoxroomInput();
        bool restoreView = focused || waiting;
        waiting = focused = false;
        if (restoreView && camera != null)
        {
            camera.transform.position = cameraPosition;
            camera.transform.rotation = cameraRotation;
        }
        if (restoreView && playerController != null)
        {
            playerController.playerCanMove = previousPlayerCanMove;
            playerController.cameraCanMove = previousCameraCanMove;
        }
        Input.ResetInputAxes();

        if (externalTexture != null) UnityEngine.Object.Destroy(externalTexture);
        externalTexture = null;
        textureAttached = false;
        ReleaseDisplay();
        try { if (nativeStarted) VRScreen_StopCapture(); } catch { }
        nativeStarted = false;
        gameWindow = IntPtr.Zero;
        camera = null;
        playerController = null;
        try { view?.Write(12, 0); } catch { }
        LoggerInstance.Msg("Captured game window closed; released the Boxroom-TV display and ended the session.");
    }

    private void ReleaseDisplay()
    {
        displayLease?.Dispose();
        displayLease = null;
    }

    private void BeginPointer()
    {
        pointerPosition = new Vector2(0.5f, 0.5f);
        lastPointerDeltaSequence = ReadInt(32);
        lastPointerDeltaTotal = new Vector2(ReadFloat(36), ReadFloat(40));
        WritePointerPosition();
        reticles.Clear();
        foreach (UIChangeableReticle reticle in Resources.FindObjectsOfTypeAll<UIChangeableReticle>())
        {
            if (reticle == null || !reticle.enabled || !reticle.gameObject.activeInHierarchy ||
                !reticle.gameObject.scene.IsValid()) continue;
            RectTransform rect = reticle.GetComponent<Image>()?.rectTransform;
            if (rect == null) continue;
            reticles.Add(new ReticleState { Transform = rect, OriginalPosition = rect.anchoredPosition });
        }
        if (displayLease?.TryGetPointerWorldPosition(pointerPosition, out Vector3 worldPointer) == true && camera != null)
        {
            Vector3 screenPoint = camera.WorldToScreenPoint(worldPointer);
            UpdatePointerVisual(new Vector2(
                Mathf.InverseLerp(camera.pixelRect.xMin, camera.pixelRect.xMax, screenPoint.x),
                Mathf.InverseLerp(camera.pixelRect.yMin, camera.pixelRect.yMax, screenPoint.y)));
        }
        LoggerInstance.Msg($"BOXROOM reticle pointer enabled ({reticles.Count} active reticle object(s)).");
    }

    private void UpdateVirtualPointer()
    {
        int sequence = ReadInt(32);
        if (sequence == lastPointerDeltaSequence) return;
        lastPointerDeltaSequence = sequence;
        Vector2 total = new(ReadFloat(36), ReadFloat(40));
        Vector2 delta = total - lastPointerDeltaTotal;
        lastPointerDeltaTotal = total;
        pointerPosition = new Vector2(
            Mathf.Clamp01(pointerPosition.x + delta.x),
            Mathf.Clamp01(pointerPosition.y + delta.y));
        WritePointerPosition();
    }

    private void WritePointerPosition()
    {
        try
        {
            view?.Write(24, pointerPosition.x);
            view?.Write(28, pointerPosition.y);
        }
        catch { }
    }

    private void ResetPointerTransport()
    {
        try
        {
            view?.Write(32, 0);
            view?.Write(36, 0f);
            view?.Write(40, 0f);
        }
        catch { }
    }

    private void UpdatePointerVisual(Vector2 reticlePosition)
    {
        foreach (ReticleState state in reticles)
        {
            if (state.Transform == null) continue;
            RectTransform parent = state.Transform.parent as RectTransform;
            Vector2 size = parent != null ? parent.rect.size : new Vector2(Screen.width, Screen.height);
            state.Transform.anchoredPosition = new Vector2(
                (reticlePosition.x - 0.5f) * size.x,
                (reticlePosition.y - 0.5f) * size.y);
        }
    }

    private void RestorePointer()
    {
        foreach (ReticleState state in reticles)
            if (state.Transform != null) state.Transform.anchoredPosition = state.OriginalPosition;
        reticles.Clear();
    }

    private void LockBoxroomInput()
    {
        UnlockBoxroomInput();
        foreach (PlayerInput playerInput in UnityEngine.Object.FindObjectsByType<PlayerInput>(FindObjectsSortMode.None))
        {
            if (playerInput == null || !playerInput.inputIsActive) continue;
            lockedPlayerInputs.Add(playerInput);
            playerInput.DeactivateInput();
        }

        object manager = Singleton<InputManager>.Instance;
        if (manager == null) return;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (FieldInfo field in manager.GetType().GetFields(flags)) DisableMapsFrom(field.GetValue(manager));
        foreach (PropertyInfo property in manager.GetType().GetProperties(flags))
        {
            if (!property.CanRead || property.GetIndexParameters().Length != 0) continue;
            try { DisableMapsFrom(property.GetValue(manager)); } catch { }
        }
    }

    private void DisableMapsFrom(object value)
    {
        if (value is InputActionMap map)
        {
            if (map.enabled && !lockedActionMaps.Contains(map))
            {
                lockedActionMaps.Add(map);
                map.Disable();
            }
        }
        else if (value is InputActionAsset asset)
        {
            foreach (InputActionMap candidate in asset.actionMaps)
            {
                if (!candidate.enabled || lockedActionMaps.Contains(candidate)) continue;
                lockedActionMaps.Add(candidate);
                candidate.Disable();
            }
        }
    }

    private void KeepBoxroomInputLocked()
    {
        foreach (InputActionMap map in lockedActionMaps)
            if (map != null && map.enabled) map.Disable();
        if (playerController != null)
        {
            playerController.playerCanMove = false;
            playerController.cameraCanMove = false;
        }
    }

    private void UnlockBoxroomInput()
    {
        foreach (InputActionMap map in lockedActionMaps)
            if (map != null) map.Enable();
        lockedActionMaps.Clear();
        foreach (PlayerInput playerInput in lockedPlayerInputs)
            if (playerInput != null) playerInput.ActivateInput();
        lockedPlayerInputs.Clear();
    }

    private void StartHost()
    {
        string exe = Path.Combine(dataRoot, "Host", "Boxroom.VRScreen.Host.exe");
        if (!File.Exists(exe)) { LoggerInstance.Warning("Window helper missing: " + exe); return; }
        try
        {
            foreach (Process existing in Process.GetProcessesByName("Boxroom.VRScreen.Host"))
            {
                try
                {
                    if (!string.Equals(existing.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase)) continue;
                    existing.Kill();
                    existing.WaitForExit(1000);
                }
                catch { }
                finally { existing.Dispose(); }
            }

            Process current = Process.GetCurrentProcess();
            hostProcess = Process.Start(new ProcessStartInfo(exe)
            {
                Arguments = "--boxroom-pid " + current.Id,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = dataRoot
            });
        }
        catch (Exception exception) { LoggerInstance.Error("Could not start window helper: " + exception.Message); }
    }

    public override void OnDeinitializeMelon()
    {
        RestorePointer();
        UnlockBoxroomInput();
        try { if (nativeStarted) VRScreen_StopCapture(); } catch { }
        if (externalTexture != null) UnityEngine.Object.Destroy(externalTexture);
        ReleaseDisplay();
        EnsureMap();
        try { view?.Write(12, Protocol.ShutdownRequested); } catch { }
        try
        {
            if (hostProcess != null && !hostProcess.WaitForExit(1500)) hostProcess.Kill();
        }
        catch { }
        finally { hostProcess?.Dispose(); hostProcess = null; }
        view?.Dispose(); map?.Dispose();
    }
}
