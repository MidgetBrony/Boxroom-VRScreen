# Boxroom-VRScreen

> **Experimental · Windows only**

Play a BOXROOM game on an in-room television or monitor. This experimental build depends on Windows Graphics Capture, Win32 window management, and an x64 Windows native plugin. Capture and input compatibility varies by game.

## Controls

1. Pick up a game box.
2. Look at a television or monitor and use the normal primary interaction.
3. The game launches, the camera faces the display, and controls pass to the game window.
4. Press **F2** to toggle between the game and BOXROOM. The game keeps running and remains visible on the display.

Version 0.3.34 routes configured emulated games to BR-Libretro and uses Boxroom-TV's shared display ownership and F2 focus broker for ordinary Steam games. Captured frames are assigned directly through the Boxroom-TV display lease, so VRScreen no longer creates a duplicate screen mesh or material and each television keeps its native bezel, geometry, UVs, and restoration lifecycle. The helper tracks BOXROOM by its process identity rather than assuming the foreground window is BOXROOM, which keeps multi-TV handoffs from terminating capture during focus changes. The real game remains full-screen and focused behind BOXROOM. The focused helper samples normalized physical mouse movement at 125 Hz and publishes cumulative movement, so BOXROOM cannot lose an intermediate sample. BOXROOM owns and clamps the absolute virtual cursor to the in-room TV, then sends that same coordinate back for mapping onto the full-screen game client. Native clicks and keyboard input continue to reach the focused game. Window discovery snapshots processes before launch, establishes the new game PID, and then accepts top-level windows only from that PID or descendants verified through the Windows parent-process chain. Desktop and chat overlays are also rejected as a final safety check. The helper reserves F2 only while the external game owns Windows focus. Returning to the room replaces the game cursor clip with a clip to BOXROOM's client area; refocusing the TV restores the game clip, and ending the session releases confinement completely. Original game and BOXROOM window styles, bounds, and topmost state are restored when the session ends. Native cursor capture is disabled to avoid a duplicate pointer.

The mod DLL targets .NET Standard 2.1. A small .NET Framework 4.8 helper handles game-window discovery and the global F2 edge toggle. `VRScreenCapture.dll` is a self-contained x64 native plugin and does not require .NET 8 or a separate Visual C++ runtime.

## Build

Install Visual Studio 2022 Build Tools with the C++ workload and Windows SDK. Copy `Directory.Build.user.props.example` to `Directory.Build.user.props`, set `GamePath`, and run:

```powershell
dotnet build Boxroom-VRScreen.csproj -c Release -p:DeployToGame=true
```

Build success proves compilation and installation only. Live testing must still confirm game-window discovery, frame capture, camera orientation, controller ownership, and F2 return behavior.
