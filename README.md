# MxAutoPair

Runs in the background on Windows and pairs Logitech MX Bluetooth mice and the Ravana PRO5.0 keyboard
automatically when they are put in pairing mode, so the "Pair device?" prompt doesn't need a click. It also
keeps Bluetooth switched on, removes leftover pairings and reinstalls a missing input driver after a device is
removed in Device Manager, re-pairs a device that has forgotten this PC (it keeps advertising but never
connects, e.g. after being paired to another PC), and recovers when the Bluetooth Support Service restarts.

- **Install:** unzip a release package and double-click `Install.cmd` (starts at sign-in, relaunches within a minute if closed).
- **Remove:** `Uninstall.cmd`.
- **Mouse wiggler** (for testing mouse-tracking software): on a random interval, a burst of mouse input following a
  scenario - random moves/clicks/scrolls, flicks onto targets with micro-adjustments, jiggle-peeks, tracking a strafing
  target, recoil-style sprays, or a mix - with burst firing, an optional delay between actions, and a simulated hand
  whose tremor and twitches scale with a stress setting. Runs on schedule regardless of other input; **Ctrl+Alt+W** toggles it.
- **Command center** (publishing PC only): `Command Center.cmd` opens a control page on `http://localhost:8790` to start/stop
  the wiggler, request a burst, pick its scenario, timing, action delay and scenario options, pause or resume
  auto-connect for the mouse and the keyboard, and turn the built-in signal sound (`assets/signal.mp3`) on or off at a chosen volume, either
  looping or as a keypress sound (plays while someone types, fading out over a set time after the last key). Settings are signed with the command key and pushed to
  the `control` branch; installed copies check it every 3 s and fit every value into their own limits.
- **Troubleshoot:** `Diagnose.cmd` writes a read-only report to `%LOCALAPPDATA%\MxAutoPair\MxAutoPair.log` and opens it.
- **Updates:** installed copies check this repo's latest release every minute, and only install
  a release whose manifest is signed with the publisher's key (`publish-update.ps1`).
