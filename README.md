# MxAutoPair

Runs in the background on Windows and pairs Logitech MX Bluetooth mice automatically when they are put in
pairing mode, so the "Pair device?" prompt doesn't need a click. It also keeps Bluetooth switched on,
removes leftover pairings and reinstalls a missing mouse driver after a device is removed in Device Manager,
and recovers when the Bluetooth Support Service restarts.

- **Install:** unzip a release package and double-click `Install.cmd` (starts at sign-in, relaunches within a minute if closed).
- **Remove:** `Uninstall.cmd`.
- **Troubleshoot:** `Diagnose.cmd` writes a read-only report to `%LOCALAPPDATA%\MxAutoPair\MxAutoPair.log` and opens it.
- **Updates:** installed copies check this repo's latest release at start and hourly, and only install
  a release whose manifest is signed with the publisher's key (`publish-update.ps1`).
