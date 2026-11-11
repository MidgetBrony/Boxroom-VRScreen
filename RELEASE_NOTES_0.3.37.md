# Boxroom-VRScreen 0.3.37

- Samples Windows Graphics Capture frames as sRGB so captured games retain their intended colour and contrast.
- Prevents a pending capture-frame callback from stalling teardown when a game is closed and another capture is started.
- Retains the BOXROOM 1.0 RGB Monitor orientation fix from 0.3.36.

Release build verification does not replace an in-game colour comparison and close/reopen test.
