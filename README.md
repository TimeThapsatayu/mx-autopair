# MxAutoPair

Runs in the background on Windows and pairs Logitech MX Bluetooth mice automatically when they are put in
pairing mode, so the "Pair device?" prompt doesn't need a click. It also keeps Bluetooth switched on,
removes leftover pairings and reinstalls a missing mouse driver after a device is removed in Device Manager,
and recovers when the Bluetooth Support Service restarts.

- **Install:** unzip a release package and double-click `Install.cmd` (starts at sign-in, relaunches within a minute if closed).
- **Remove:** `Uninstall.cmd`.
- **Mouse wiggler** (for testing mouse-tracking software): on a random interval, a burst of mouse input following a
  scenario - random moves/clicks/scrolls, flicks onto targets, tracking a strafing target, recoil-style sprays, or a
  mix - with an optional delay between actions. Runs on schedule regardless of other input; **Ctrl+Alt+W** toggles it.
- **Command center** (publishing PC only): `Command Center.cmd` opens a control page on `http://localhost:8790` to start/stop
  the wiggler, request a burst, and pick its scenario, timing, action delay and scenario options. Settings are signed with the command key and pushed to
  the `control` branch; installed copies check it every 3 s and fit every value into their own limits.
- **Troubleshoot:** `Diagnose.cmd` writes a read-only report to `%LOCALAPPDATA%\MxAutoPair\MxAutoPair.log` and opens it.
- **Updates:** installed copies check this repo's latest release every minute, and only install
  a release whose manifest is signed with the publisher's key (`publish-update.ps1`).
