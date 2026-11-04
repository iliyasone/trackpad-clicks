# TrackpadClicks

Mac-like physical-click gestures for a precision touchpad on Windows, made for an
Apple Magic Trackpad. Windows can only bind three- and four-finger *taps* to actions;
TrackpadClicks binds them to real *clicks*, so a light touch never triggers anything.

The one-finger edge clicks and the three- and four-finger clicks are set up in the
settings window (click the tray icon). It draws the pad with its six edge zones and
shows your fingers on it live; pick a zone or a finger count, then its action:

- middle mouse button, held while the pad is held;
- dictation button: mouse button 5 ("forward") held while the pad is held, to bind
  as the dictation trigger in Ciao and keep the middle button free for apps. After a
  hold it stays down at least 0.45 s, so Ciao never takes it for a short tap (which
  would start hands-free dictation). A quick double click on that zone (with the hold
  trigger) does tap it: hands-free dictation on. While Ciao says it is on (its
  `state.json`), a single click there taps it again: off. The first click of the pair
  stays a normal click;
- play/pause, next track, previous track;
- play/pause for everything but the music app: pauses whatever plays (the agent's voice
  in T3 Code, a voice message in Telegram), else resumes what played last;
- volume up, volume down (a step; held, it repeats), mute;
- for T3 Code: send (Enter in its window), stop the agent (Ctrl+Alt+Backspace, which
  needs `{"key": "mod+alt+backspace", "command": "thread.stop"}` in T3's
  `keybindings.json`), and play/pause the agent's voice reply (T3 Code's media session);
- nothing — the click goes to Windows as usual.

Each also has a trigger. **On click** acts on the press. **On hold** leaves a short
click a normal click and acts only once the pad is held down for the hold time
(400 ms by default). The pad has one button and a click is a click whatever the force,
so a hold is the way to tell a deliberate press from a click. The width of each edge (by default 30%
of the pad on the left, 20% on the right), the height of each edge's middle zone (a
third by default; the top and bottom zones share the rest) and the hold time are set
there too.

**Second press.** While an edge zone holds the middle button, the pad's one button is
already down, so pressing another finger makes no new click. With Magic Trackpad
driver 2.2 or later, which reports each finger's pressure, another finger pressing past
the set force presses the left button, two fingers the right one, until they ease off.
On by default; the force is a slider. The test panel shows each finger's pressure.
With a driver that has the haptic command proposed in
[MagicTrackpad2ForWindows#63](https://github.com/vitoplantamura/MagicTrackpad2ForWindows/pull/63),
the pad also clicks under the finger on the second press and its release.

**Music.** Media keys go to whichever app Windows considers current, often the agent's
voice instead of the music. Play/pause and the track actions therefore go to one app's
media session, Chrome by default, chosen in the window; "any" behaves like a media key.

**Тест** in the window's header shows what the program sees: the button, the finger
count, the last gestures and what they did, and the raw bytes of each report, with the
bytes that change flashing — press harder without moving a finger to see whether
anything in the report follows the force. Settings are saved at once to
`%APPDATA%\TrackpadClicks\config.json`.

Defaults:

| Click | What it does |
| --- | --- |
| 3 fingers | Middle mouse button |
| 4 fingers | Play/pause for everything but the music (T3 Code, Telegram…) |
| 1 finger, left edge, top, hold | Next track |
| 1 finger, left edge, middle, hold | Dictation button (push-to-talk in Ciao); a double click: hands-free on, then one click: off |
| 1 finger, left edge, bottom, hold | Previous track |
| 1 finger, right edge, top | Volume up a step; hold to repeat |
| 1 finger, right edge, middle | Play/pause the music (Chrome) |
| 1 finger, right edge, bottom | Volume down a step; hold to repeat |

Two-finger clicks are not configurable:

| Click | What it does |
| --- | --- |
| 2 fingers, hold, both fingers slide | Drag the window under the cursor, grabbed from any point |
| 2 fingers, hold, one finger stays, one slides | Normal selection drag, as without TrackpadClicks |
| 2 fingers, release in place | Right click when both fingers came down together, however far apart; left when one was resting (a thumb) |

Fingers resting on the pad before a click are told apart by when each finger landed.
Two or more fingers put down together (within 150 ms) make the gesture along with
any finger already resting, so pointing with one finger and clicking with two more is
a three-finger click. A single finger added to resting ones more than 0.4 s after
them is a click by that finger alone: in an edge zone it is that zone's gesture and
nothing else, wherever the other fingers rest; elsewhere it goes to Windows as usual.

A thumb resting on the pad while two fingers click does not make the click a
three-finger one: an older finger counts only if it moved shortly before. One- and
two-finger clicks away from the edges work as usual.

## Requirements

- Windows 11 with the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0).
  The settings window uses the Microsoft Edge WebView2 Runtime, which Windows 11 has.
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
off, and red when TrackpadClicks got stuck and switched itself off until the stuck thread moves on
(see below).

`scripts/deploy.sh` builds and installs to `C:\TrackpadClicks` on a remote PC over
SSH. It is specific to one setup: the PC is reached as `ssh wsl` then `ssh windows`,
and the logon task runs as user `iliya`. Adapt both before using it.

## Updates

Nothing is installed while you work. TrackpadClicks checks the latest GitHub release a
minute after start and every 6 hours (or **Проверить обновления** in the tray menu) and
downloads a newer one into `C:\TrackpadClicks\update`. Or `scripts/stage-update.sh "notes" [driver-dir
driver-version]` builds and puts the new exe, and optionally a Magic Trackpad driver
package, into `C:\TrackpadClicks\update`. The tray shows a notice, and the menu and the
settings window get an **Установить** button; a `trackpadclicks://update` link (Win+R
works too) does the same. Installing runs `pnputil` for the driver (Windows asks for
admin rights), swaps the exe and restarts it. A driver folder with an `install.ps1`
runs that instead: `scripts/test-driver/install.ps1` installs a test-signed driver from a
fork's CI build (test mode, Secure Boot off; BitLocker is suspended for the one restart).

`scripts/deploy.sh` still installs at once, for the first install or when nobody is at
the PC.

## Logs

`%LOCALAPPDATA%\TrackpadClicks\log.txt`: start-up, touchpads found, every gesture,
and any stuck thread. Creating an empty file `C:\TrackpadClicks\debug` adds every
button press and release per device, and the settings window's developer tools (F12);
it is read at start-up.

## How it works

TrackpadClicks reads the touchpads' raw HID reports (finger count, finger positions,
button state) and installs a low-level mouse hook to swallow the left/right press
Windows synthesizes for a gesture's click. It runs four threads:

- **mouse hook** (`MouseHook.cs`): every mouse event in the system waits for it, so
  it only decides swallow or pass from state in memory, never calling anything that
  can block;
- **raw input** (`RawInput.cs`): parses the HID reports;
- **effects** (`Effects.cs`): SendInput, moving windows, media sessions (`Media.cs`),
  T3 Code's keys, the haptic tick, the log;
- **UI** (`Program.cs`): the tray icon and the settings window (`SettingsWindow.cs`,
  a WebView2 page from `ui/settings.html` embedded in the exe; open that file in a
  browser to see it with a demo finger). If the raw input or effects thread is busy
  on one call for over 2 s, it switches gestures off, turns the icon red and logs the
  call. The mouse keeps working as a plain touchpad, and gestures come back by
  themselves once the thread moves on (**Restart** in the menu, if it never does).

Thread pool threads read Ciao's `state.json` (`Ciao.cs`) and download updates from
GitHub (`Updater.cs`).

What each click means is in `Gestures.cs`, with the bindings from `Settings.cs`. Quirks of the Magic Trackpad driver that
the parsing works around are commented in `RawInput.cs`.

## License

MIT, see [LICENSE](LICENSE).
