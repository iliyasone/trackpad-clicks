# TrackpadClicks

Mac-like physical-click gestures for a precision touchpad on Windows, made for an
Apple Magic Trackpad. Windows can only bind three- and four-finger *taps* to actions;
TrackpadClicks binds them to real *clicks*, so a light touch never triggers anything.

| Click | What it does |
| --- | --- |
| 3 fingers | Middle mouse button |
| 1 finger, left edge, top third | Volume up a step; hold to repeat |
| 1 finger, left edge, middle third | Media play/pause |
| 1 finger, left edge, bottom third | Volume down a step; hold to repeat |
| 1 finger, right edge, top third | Next track |
| 1 finger, right edge, middle third | Middle mouse button (e.g. push-to-talk voice input) |
| 1 finger, right edge, bottom third | Previous track |
| 2 fingers, hold, both fingers slide | Drag the window under the cursor, grabbed from any point |
| 2 fingers, hold, one finger stays, one slides | Normal selection drag, as without TrackpadClicks |
| 2 fingers, release in place | Normal click (right or left, as Windows decided) |

Fingers resting on the pad before a click are told apart by when each finger landed.
Two or more fingers put down together (within 150 ms) make the gesture along with
any finger already resting, so pointing with one finger and clicking with two more is
a three-finger click. A single finger added to resting ones more than 0.4 s after
them is a click by that finger alone and goes to Windows as usual.

Each edge is a strip a fifth of the pad's width: the left one up to 20%, the right one from 80%. A thumb resting on the pad
while two fingers click does not make the click a three-finger one: an older finger
counts only if it moved shortly before. Four-finger clicks are left to Windows.
One- and two-finger clicks otherwise work as usual.

## Requirements

- Windows 11 with the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0).
- The touchpad must be a Windows *precision touchpad*. A Magic Trackpad becomes one
  with the [mac-precision-touchpad](https://github.com/imbushuo/mac-precision-touchpad)
  driver.
- Taps turned off in **Settings → Bluetooth & devices → Touchpad → Taps**, and
  three- and four-finger taps set to **Nothing** — TrackpadClicks only handles clicks.
  Under **Advanced gestures**, set the four-finger swipes up and down to **Nothing**,
  or Windows changes the volume on a plain swipe as well.

## Build

```sh
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o out
```

Builds on Linux too (`EnableWindowsTargeting`). The result is `out/TrackpadClicks.exe`.

## Run

Start `TrackpadClicks.exe` in the signed-in user's session. To start it at logon,
create a Task Scheduler task with an *At log on* trigger, running as that user with
*Run only when user is logged on*. Only one copy runs at a time; extra copies exit.

The tray icon (a small trackpad) shows the state and has a menu: turn gestures on or
off, open the log, restart, exit. The icon is white when gestures are on, grey when
off, and red when TrackpadClicks got stuck and switched itself off (see below).

`scripts/deploy.sh` builds and installs to `C:\TrackpadClicks` on a remote PC over
SSH. It is specific to one setup: the PC is reached as `ssh wsl` then `ssh windows`,
and the logon task runs as user `iliya`. Adapt both before using it.

## Logs

`%LOCALAPPDATA%\TrackpadClicks\log.txt`: start-up, touchpads found, every gesture,
and any stuck thread. Creating an empty file `C:\TrackpadClicks\debug` adds every
button press and release per device; it is read at start-up.

## How it works

TrackpadClicks reads the touchpads' raw HID reports (finger count, finger positions,
button state) and installs a low-level mouse hook to swallow the left/right press
Windows synthesizes for a gesture's click. It runs four threads:

- **mouse hook** (`MouseHook.cs`): every mouse event in the system waits for it, so
  it only decides swallow or pass from state in memory, never calling anything that
  can block;
- **raw input** (`RawInput.cs`): parses the HID reports;
- **effects** (`Effects.cs`): SendInput, moving windows, the log;
- **UI** (`Program.cs`): the tray icon. If the raw input or effects thread is busy
  on one call for over 2 s, it switches gestures off, turns the icon red and logs the
  call. The mouse keeps working as a plain touchpad; **Restart** in the menu brings
  gestures back.

What each click means is in `Gestures.cs`. Quirks of the Magic Trackpad driver that
the parsing works around are commented in `RawInput.cs`.
