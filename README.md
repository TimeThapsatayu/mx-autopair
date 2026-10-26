# MxAutoPair

Runs in the background on Windows and pairs Logitech MX Bluetooth mice automatically when they are put in
pairing mode, so the "Pair device?" prompt doesn't need a click. It also keeps Bluetooth switched on,
removes leftover pairings and reinstalls a missing mouse driver after a device is removed in Device Manager,
and recovers when the Bluetooth Support Service restarts.

- **Install:** unzip a release package and double-click `Install.cmd` (starts at sign-in, relaunches within a minute if closed).
- **Remove:** `Uninstall.cmd`.
- **Mouse wiggler** (for testing mouse-tracking software): every 30-90 s, a 0.5-3 s burst of random moves across the screen,
  left/right clicks and scrolls, on schedule regardless of other input. **Ctrl+Alt+W** turns it on or off.
- **Troubleshoot:** `Diagnose.cmd` writes a read-only report to `%LOCALAPPDATA%\MxAutoPair\MxAutoPair.log` and opens it.
- **Updates:** installed copies check this repo's latest release at start and every 10-20 minutes, and only install
  a release whose manifest is signed with the publisher's key (`publish-update.ps1`).
