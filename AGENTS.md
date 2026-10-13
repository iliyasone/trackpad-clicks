# TrackpadClicks: rules for agents

TrackpadClicks is a Windows-only tray app (C#, .NET 8, WinForms) that turns clicks on a
precision touchpad — made for an Apple Magic Trackpad — into gestures. The README says what
each click does and how the threads are split; read its "How it works" first.

## Rules

- Nothing may slow the mouse. The low-level hook (`MouseHook.cs`, `Gestures.OnMouse`) runs
  for every mouse event in the system: it only reads and writes memory under
  `Gestures.Sync`. SendInput, files, the log and window calls go through `Effects` /
  `Worker`, never straight from the hook or the raw input thread.
- A user-visible gesture change updates the README table and the comment at the top of
  `Gestures.cs` in the same commit. A new action or setting goes into `Settings.cs`,
  `Gestures.Begin` and `ui/settings.html` (`NAMES`, `ICONS`) together.
- The UI and tray texts are in Russian; code, comments, logs and the README in English.
- The Magic Trackpad driver reports only contact ID, X, Y, contact count and the button —
  no pressure or contact size. Don't build on force; a hold is the stand-in.

## Checking a change

- `~/.dotnet/dotnet build -c Release` builds on Linux (`EnableWindowsTargeting`); it must
  stay free of warnings.
- `ui/settings.html` opens in any browser with a demo finger; screenshot it there after a
  UI change.
- Gestures can only be tried on a real pad: `scripts/deploy.sh` installs on Ilias's PC and
  prints the log's tail. Ask him to try the gesture, then read
  `%LOCALAPPDATA%\TrackpadClicks\log.txt` over the same SSH hop.
