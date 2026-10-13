// What each physical click means. All of it runs under Sync, called from the raw input
// thread (finger reports) and the mouse hook (button events), and only changes state
// in memory; anything that touches the system goes through Effects.
//
// Fingers resting on the pad before the click are told apart by when each landed and
// whether it moved since; see ClickFingers. With two fingers resting, a click by a
// third goes to Windows.
//
// What a click does comes from the settings (Settings.cs): a click with 3 or with 4
// fingers, and a click with 1 finger (a thumb, say) in one of six edge zones — the left
// or right strip of the pad, split in three by height (the middle one's height set per side). Each gets an action: the middle
// button or mouse button 5 (held while the pad is; after a Hold trigger, for at least
// MinHeldMs, so an app never takes a held gesture for a short tap), a media key, a volume step (repeating while held like
// a held key), a T3 Code agent action (send, stop, voice), or nothing, left to Windows. With the Hold trigger the press is held back:
// released before HoldMs it is replayed as the click it was, held longer it is the action.
//
// Second press: while an edge zone holds a mouse button, the pad's one button is
// already down, so another finger pressing gives no new click. With driver 2.2+ each
// finger reports its pressure: another finger pressing past PressForce presses the left
// button (two fingers: the right one) until it eases off to 60% of that.
// Click with 2 fingers: Windows makes it a left press (one finger holds the click, the
// other drags a selection) or a right click, and which one the user means is not known
// at press time. So the press is held back and the fingers decide:
//   both fingers travel        -> drag the window under the cursor, from any point of it;
//   one travels, one stays     -> the press is replayed, a normal selection;
//   released without travel    -> a click: right when both fingers came down together
//                                 (Windows makes two fingers far apart a left click, as
//                                 if one were a resting thumb), else the one Windows made.
// The left/right press Windows makes for these clicks is swallowed by the hook.

using static TrackpadClicks.Native;

namespace TrackpadClicks;

sealed class Gestures
{
    // Off from the tray, or when a thread gets stuck. Read by the hook without the lock.
    public static volatile bool Enabled = true;

    public readonly object Sync = new();
    public readonly List<Pad> Pads = new();

    const long FreshMs = 250;
    // Fingers landing within this of each other were put down together (observed:
    // 0-60 ms for one motion, hundreds of ms for a finger added later).
    const long TogetherMs = 150;
    // A lone finger landing within this of the ones before still belongs to the same
    // motion; later, they were resting and it is a click by that finger alone.
    const long SameMotionMs = 400;
    // An older finger that moved within this before the last landing was pointing,
    // not resting, and is counted in the click.
    const long PointingMs = 1500;
    // Held top/bottom click: first repeat after this, then one step per RepeatMs.
    const int RepeatDelayMs = 350, RepeatMs = 60;
    // Finger travel, as a share of the pad's width, that counts as moving (~4 mm).
    const double FingerMoves = 0.025;
    // Full pad width moves the window this many primary-screen widths.
    const double DragGain = 1.5;
    // Shell windows that keep their own clicks.
    static readonly HashSet<string> Unmovable = new() { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };

    // Waiting: a Hold gesture pressed, not yet held long enough. Button: a mouse button held.
    enum Mode { None, Waiting, Button, Key }
    Mode _mode;
    Pad? _modePad;
    string _slot = "";
    GestureAction _action;
    bool _swallowing;
    // The button Windows pressed for a Waiting gesture, once the hook has seen it.
    bool _pressSeen, _pressRight;
    readonly System.Threading.Timer _hold;

    // T3 Code's media session id contains this (com.t3tools.t3code, or the exe name).
    const string AgentVoiceApp = "t3";

    // Second press: the finger holding the edge, the button pressed by others (0 none,
    // 1 left, 2 right), and since when one finger presses (to wait for a second one).
    uint _holdFinger;
    int _second;
    long _secondSince;
    const long SecondPairMs = 60;

    // The mouse button a Button gesture holds, when it went down, and whether its up is
    // put off to MinHeldMs. Ciao takes a press under 0.35 s for a tap that starts
    // hands-free dictation; a gesture that needed a hold must never look like one.
    uint _buttonDown, _buttonUp, _buttonData;
    long _buttonAt;
    bool _buttonHeld, _upPending;
    readonly System.Threading.Timer _buttonRelease;
    const long MinHeldMs = 450;

    // The volume key held, or 0.
    ushort _repeatKey;
    readonly System.Threading.Timer _repeat;

    enum Drag { None, Pending, Moving, Passing }
    Drag _drag;
    Pad? _dragPad;
    bool _dragPadPressed;
    bool _dragRight;
    // The click a press released in place becomes; see the comment at the top.
    bool _clickRight;
    bool _upSeen, _swallowUp;
    POINT _dragFrom;
    long _dragStart;
    readonly Dictionary<uint, (double X, double Y)> _fingerStart = new();
    double _fingerX, _fingerY, _offX, _offY;
    int _fingerCount;
    readonly double _screenWidth = Screen.PrimaryScreen!.Bounds.Width;

    public Gestures()
    {
        _repeat = new System.Threading.Timer(_ =>
        {
            lock (Sync)
                if (_repeatKey != 0 && Enabled) Effects.Key(_repeatKey);
        });
        _buttonRelease = new System.Threading.Timer(_ =>
        {
            lock (Sync)
                if (_upPending)
                {
                    _upPending = false;
                    Effects.Mouse(_buttonUp, _buttonData);
                }
        });
        _hold = new System.Threading.Timer(_ =>
        {
            lock (Sync)
                if (_mode == Mode.Waiting && Enabled) Begin();
        });
    }

    // Drop whatever is in progress; for switching off.
    public void Reset()
    {
        _repeat.Change(Timeout.Infinite, Timeout.Infinite);
        _hold.Change(Timeout.Infinite, Timeout.Infinite);
        EndSecond();
        if (_mode == Mode.Button || _upPending)
        {
            _buttonRelease.Change(Timeout.Infinite, Timeout.Infinite);
            _upPending = false;
            Effects.Mouse(_buttonUp, _buttonData);
        }
        _mode = Mode.None;
        _modePad = null;
        _repeatKey = 0;
        _swallowing = false;
        _drag = Drag.None;
        _dragPad = null;
        _swallowUp = false;
    }

    // ---- Finger reports.

    public void OnReport(Pad pad, bool button)
    {
        if (!Enabled)
        {
            pad.Button = button;
            return;
        }
        if (button && !pad.Button && Find(pad) is string slot)
        {
            Start(pad, slot);
        }
        else if (button && !pad.Button && pad.Contacts >= 3 && _mode == Mode.None)
        {
            Note(pad.Contacts >= 4 ? "four" : "three", "windows");
            Log.Write($"{pad.Contacts} fingers, counted {ClickFingers(pad)} (landed {pad.LandingsText} ms before the last), left to Windows");
        }
        else if (!button && pad.Button && pad == _modePad)
        {
            End();
        }
        pad.Button = button;
        if (_mode == Mode.Button && pad == _modePad && _slot.Contains('-')) SecondPress(pad);

        if (_drag != Drag.None && pad == _dragPad)
        {
            // The hook can see the press before the pad's report does: only a release
            // after a seen press ends the drag.
            if (button) _dragPadPressed = true;
            if (!button && _dragPadPressed) EndDrag("pad release");
            else if (_drag == Drag.Pending) Decide(pad);
            else if (_drag == Drag.Moving) MoveWindow(pad);
        }
    }

    // ---- Mouse events. Returns true to swallow.

    public bool OnMouse(int msg, MSLLHOOKSTRUCT info)
    {
        if (_mode == Mode.None && DragMouse(msg, info)) return true;
        if (msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN)
        {
            if (_modePad == null && FreshPad(p => Find(p) != null) is Pad pad) Start(pad, Find(pad)!);
            if (_modePad != null)
            {
                _swallowing = true;
                if (!_pressSeen)
                {
                    _pressSeen = true;
                    _pressRight = msg == WM_RBUTTONDOWN;
                }
                return true;
            }
        }
        else if (msg is WM_LBUTTONUP or WM_RBUTTONUP && _swallowing)
        {
            _swallowing = false;
            End();
            return true;
        }
        return false;
    }

    // ---- One-finger edge clicks and three/four-finger clicks.

    // How many fingers a click is made with, by how they landed. The last fingers to
    // land within TogetherMs of each other are the fresh group:
    //   3+ fresh             -> that many; a finger resting before (a thumb) is left out;
    //   2 fresh              -> those two plus the older fingers that were moving: one
    //                           finger pointing, two added to click. A still one is a
    //                           resting thumb, and the click is a two-finger one;
    //   1 fresh, soon after  -> every finger: a slow but single motion;
    //   1 fresh, later       -> 1: the others rest, the click is that finger's.
    static int ClickFingers(Pad pad)
    {
        var before = pad.Landings(); // longest first, the last finger at 0
        int fresh = before.Count(ms => ms <= TogetherMs);
        if (fresh >= 3) return fresh;
        if (fresh == 2)
        {
            long last = pad.LastLanding;
            return 2 + pad.MovedSince(last - PointingMs, last - TogetherMs);
        }
        return before.Length >= 2 && before[^2] <= SameMotionMs ? before.Length : 1;
    }

    // The gesture slot a click on the pad would be, if its action is not left to Windows.
    static string? Find(Pad pad)
    {
        var config = Settings.Current;
        string? slot = pad.Contacts == 1 ? EdgeSlot(pad, config)
            : pad.Contacts >= 3 ? ClickFingers(pad) switch { 3 => "three", 4 => "four", _ => null }
            : null;
        return slot != null && config.For(slot).Action != GestureAction.Windows ? slot : null;
    }

    // The edge zone under a single finger, or null away from the edges.
    public static string? EdgeSlot(Pad pad, Config config)
    {
        if (pad.YFingers != 1) return null;
        double x = (pad.Xs[0] - pad.XMin) / Math.Max(1, pad.XMax - pad.XMin);
        string side = x <= config.LeftEdge / 100.0 ? "left" : x >= 1 - config.RightEdge / 100.0 ? "right" : "";
        if (side == "") return null;
        // Y grows downward; the middle zone is centered, its height set per side.
        double y = (pad.Ys[0] - pad.YMin) / Math.Max(1, pad.YMax - pad.YMin);
        double edge = (1 - (side == "left" ? config.LeftMiddle : config.RightMiddle) / 100.0) / 2;
        return side + (y < edge ? "-top" : y > 1 - edge ? "-bottom" : "-middle");
    }

    void Start(Pad pad, string slot)
    {
        if (_mode != Mode.None) return;
        var binding = Settings.Current.For(slot);
        _modePad = pad;
        _slot = slot;
        _action = binding.Action;
        _pressSeen = false;
        _holdFinger = pad.Ids[0];
        _second = 0;
        _secondSince = 0;
        if (binding.Trigger == Trigger.Hold)
        {
            _mode = Mode.Waiting;
            _hold.Change(Settings.Current.HoldMs, Timeout.Infinite);
        }
        else Begin();
    }

    // The action itself. A media key goes at once; a volume step repeats while held;
    // the middle button is held while the pad is.
    void Begin()
    {
        string fingers = _slot is "three" or "four" ? $" (landed {_modePad?.LandingsText} ms before the last)" : "";
        Log.Write($"{_slot}: {_action}{fingers}");
        Note(_slot, "action");
        if (_action is GestureAction.MiddleClick or GestureAction.DictationButton)
        {
            _mode = Mode.Button;
            bool x = _action == GestureAction.DictationButton;
            uint down = x ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_MIDDLEDOWN;
            // The same button still down from a press let go too soon: it just stays down.
            bool stillDown = _upPending && down == _buttonDown;
            if (_upPending && !stillDown) Effects.Mouse(_buttonUp, _buttonData);
            _buttonRelease.Change(Timeout.Infinite, Timeout.Infinite);
            _upPending = false;
            _buttonDown = down;
            _buttonUp = x ? MOUSEEVENTF_XUP : MOUSEEVENTF_MIDDLEUP;
            _buttonData = x ? XBUTTON2 : 0;
            _buttonHeld = Settings.Current.For(_slot).Trigger == Trigger.Hold;
            _buttonAt = Environment.TickCount64;
            if (!stillDown) Effects.Mouse(_buttonDown, _buttonData);
            return;
        }
        _mode = Mode.Key;
        switch (_action)
        {
            case GestureAction.AgentSend: Effects.T3Keys("send", VK_RETURN); return;
            case GestureAction.AgentStop: Effects.T3Keys("stop", VK_CONTROL, VK_MENU, VK_BACK); return;
            case GestureAction.AgentVoice: Effects.MediaKey(AgentVoiceApp, GestureAction.PlayPause); return;
            case GestureAction.OtherPlayPause: Effects.OtherPlayPause(Settings.Current.MediaApp); return;
        }
        ushort key = _action switch
        {
            GestureAction.PlayPause => VK_MEDIA_PLAY_PAUSE,
            GestureAction.NextTrack => VK_MEDIA_NEXT_TRACK,
            GestureAction.PrevTrack => VK_MEDIA_PREV_TRACK,
            GestureAction.VolumeUp => VK_VOLUME_UP,
            GestureAction.VolumeDown => VK_VOLUME_DOWN,
            _ => VK_VOLUME_MUTE,
        };
        if (key is VK_MEDIA_PLAY_PAUSE or VK_MEDIA_NEXT_TRACK or VK_MEDIA_PREV_TRACK && Settings.Current.MediaApp is { Length: > 0 } app)
            Effects.MediaKey(app, _action);
        else Effects.Key(key);
        if (key is VK_VOLUME_UP or VK_VOLUME_DOWN)
        {
            _repeatKey = key;
            _repeat.Change(RepeatDelayMs, RepeatMs);
        }
    }

    void End()
    {
        if (_mode == Mode.Waiting)
        {
            // Let go before HoldMs: an ordinary click.
            _hold.Change(Timeout.Infinite, Timeout.Infinite);
            if (_pressSeen) Effects.Click(_pressRight);
            Note(_slot, "early");
            Log.Write($"{_slot}: released before the hold, {(_pressRight ? "right" : "left")} click");
        }
        EndSecond();
        if (_mode == Mode.Button)
        {
            long left = MinHeldMs - (Environment.TickCount64 - _buttonAt);
            if (_buttonHeld && left > 0)
            {
                _upPending = true;
                _buttonRelease.Change(left, Timeout.Infinite);
            }
            else Effects.Mouse(_buttonUp, _buttonData);
        }
        _repeat.Change(Timeout.Infinite, Timeout.Infinite);
        _repeatKey = 0;
        _mode = Mode.None;
        _modePad = null;
    }

    void SecondPress(Pad pad)
    {
        var config = Settings.Current;
        if (!config.SecondPress) return;
        double threshold = _second == 0 ? config.PressForce : config.PressForce * 0.6;
        int pressing = 0;
        for (int i = 0; i < pad.YFingers; i++)
            if (pad.Ids[i] != _holdFinger && pad.Pressures[i] >= threshold) pressing++;
        long now = Environment.TickCount64;
        if (_second != 0)
        {
            if (pressing == 0) EndSecond();
            return;
        }
        if (pressing == 0)
        {
            _secondSince = 0;
            return;
        }
        if (_secondSince == 0) _secondSince = now;
        // One finger: wait a moment for a second one, which makes it a right click.
        if (pressing < 2 && now - _secondSince < SecondPairMs) return;
        _second = pressing >= 2 ? 2 : 1;
        Effects.Mouse(_second == 2 ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_LEFTDOWN);
        if (config.Haptic) Effects.Haptic(0x060617);
        Log.Write($"{_slot}: second press, {(_second == 2 ? "right" : "left")} down");
        Note(_slot, _second == 2 ? "second-right" : "second-left");
    }

    void EndSecond()
    {
        if (_second != 0)
        {
            Effects.Mouse(_second == 2 ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_LEFTUP);
            // A lighter click on release, as the pad does for its own button.
            if (Settings.Current.Haptic) Effects.Haptic(0x040415);
        }
        _second = 0;
        _secondSince = 0;
    }

    // ---- For the settings window: the pad touched last, its fingers, the gesture
    // pressed on it, and the last gestures.

    readonly List<GestureEvent> _events = new();
    int _eventSeq;

    // What: "action" (the gesture fired), "early" (a hold let go early: a normal click),
    // "windows" (a 3+ finger click left to Windows), "second-left"/"second-right".
    void Note(string slot, string what)
    {
        var action = Settings.Current.For(slot).Action;
        _events.Add(new GestureEvent(++_eventSeq, slot, what, action));
        if (_events.Count > 8) _events.RemoveAt(0);
    }

    public Live? Snapshot()
    {
        var pad = Pads.Where(p => p.XMax > 0).MaxBy(p => p.LastReport);
        if (pad == null) return null;
        double w = Math.Max(1, pad.XMax - pad.XMin), h = Math.Max(1, pad.YMax - pad.YMin);
        var fingers = new double[pad.Contacts > 0 ? Math.Min(pad.YFingers, Pad.MaxFingers) : 0][];
        for (int i = 0; i < fingers.Length; i++)
            fingers[i] = new[] { (pad.Xs[i] - pad.XMin) / w, (pad.Ys[i] - pad.YMin) / h, pad.Pressures[i] };
        string? active = pad == _modePad ? _slot : null;
        return new Live(w / h, fingers, pad.Button, active, _mode == Mode.Waiting,
            pad.Contacts == 1 ? EdgeSlot(pad, Settings.Current) : null, pad.Contacts,
            _events.ToArray(), pad.Raw == null ? null : Convert.ToHexString(pad.Raw));
    }

    // ---- Two fingers.

    bool DragMouse(int msg, MSLLHOOKSTRUCT info)
    {
        bool down = msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN;
        bool up = msg is WM_LBUTTONUP or WM_RBUTTONUP;

        if (_drag == Drag.None)
        {
            // The drag ended on the pad's own release before Windows sent its button up.
            if (up && _swallowUp)
            {
                _swallowUp = false;
                return true;
            }
            var pad = down ? FreshPad(p => p.Contacts == 2) : null;
            if (pad == null || !Movable(info.X, info.Y)) return false;
            _drag = Drag.Pending;
            _dragPad = pad;
            _dragPadPressed = pad.Button;
            _dragRight = msg == WM_RBUTTONDOWN;
            _clickRight = _dragRight || ClickFingers(pad) == 2;
            _upSeen = _swallowUp = false;
            _dragFrom = new POINT { X = info.X, Y = info.Y };
            _dragStart = Environment.TickCount64;
            _fingerStart.Clear();
            for (int i = 0; i < pad.YFingers; i++)
                _fingerStart[pad.Ids[i]] = (pad.Xs[i], pad.Ys[i]);
            return true;
        }

        if (_drag == Drag.Passing)
        {
            if (up) EndDrag("button up");
            return false;
        }

        switch (msg)
        {
            case WM_MOUSEMOVE:
                // While moving, the cursor follows the fingers, not Windows.
                return _drag == Drag.Moving;
            case WM_MOUSEWHEEL or WM_MOUSEHWHEEL:
                return true;
        }
        if (down) return true;
        if (up)
        {
            // Windows may let go early while the pad is still pressed; the pad's own
            // release ends the drag then.
            _upSeen = true;
            if (!(_dragPadPressed && _dragPad is { Button: true })) EndDrag("button up");
            return true;
        }
        return false;
    }

    void Decide(Pad pad)
    {
        double width = Math.Max(1, pad.XMax - pad.XMin);
        int moving = 0, still = 0;
        for (int i = 0; i < pad.YFingers; i++)
        {
            if (!_fingerStart.TryGetValue(pad.Ids[i], out var start))
            {
                _fingerStart[pad.Ids[i]] = (pad.Xs[i], pad.Ys[i]);
                continue;
            }
            double travel = Math.Sqrt(Math.Pow(pad.Xs[i] - start.X, 2) + Math.Pow(pad.Ys[i] - start.Y, 2)) / width;
            if (travel >= FingerMoves) moving++;
            else if (travel < FingerMoves / 2) still++;
        }

        if (moving >= 2)
        {
            _drag = Drag.Moving;
            _fingerX = pad.X;
            _fingerY = pad.Y;
            _fingerCount = pad.YFingers;
            _offX = _offY = 0;
            Effects.DragBegin(WindowAt(_dragFrom.X, _dragFrom.Y), _dragFrom);
        }
        else if (moving == 1 && (still >= 1 || pad.YFingers == 1))
        {
            _drag = Drag.Passing;
            Effects.ReplayPress(_dragFrom, _dragRight);
        }
    }

    void MoveWindow(Pad pad)
    {
        if (pad.YFingers == 0) return;
        // A finger landing or lifting jumps the average; measure afresh from here.
        if (pad.YFingers != _fingerCount)
        {
            _fingerX = pad.X;
            _fingerY = pad.Y;
            _fingerCount = pad.YFingers;
            return;
        }
        double scale = _screenWidth * DragGain / Math.Max(1, pad.XMax - pad.XMin);
        _offX += (pad.X - _fingerX) * scale;
        _offY += (pad.Y - _fingerY) * scale;
        _fingerX = pad.X;
        _fingerY = pad.Y;
        Effects.DragTo(_offX, _offY);
    }

    void EndDrag(string by)
    {
        if (_drag == Drag.Pending) Effects.Click(_clickRight);
        Log.Write($"two-finger {(_dragRight ? "right" : "left")} press -> {_drag switch { Drag.Moving => "window drag", Drag.Passing => "selection", _ => _clickRight ? "right click" : "left click" }}"
                  + $", {Environment.TickCount64 - _dragStart} ms, ended by {by}");
        // In Passing the real button up went through to the app already.
        _swallowUp = _drag != Drag.Passing && !_upSeen;
        _drag = Drag.None;
        _dragPad = null;
    }

    Pad? FreshPad(Func<Pad, bool> match)
    {
        long now = Environment.TickCount64;
        foreach (var pad in Pads)
            if (match(pad) && now - pad.LastReport < FreshMs) return pad;
        return null;
    }

    // WindowFromPoint and GetClassName read window data without messaging the window,
    // so they are safe in the hook.
    static IntPtr WindowAt(int x, int y) => GetAncestor(WindowFromPoint(new POINT { X = x, Y = y }), GA_ROOT);

    static bool Movable(int x, int y)
    {
        var window = WindowAt(x, y);
        if (window == IntPtr.Zero) return false;
        var name = new System.Text.StringBuilder(64);
        GetClassName(window, name, name.Capacity);
        return !Unmovable.Contains(name.ToString());
    }
}

// One frame of the live view: pad width/height, fingers as 0..1 positions plus pressure
// (-1: not reported), whether it is
// pressed, the gesture slot pressed (Waiting: still within its hold), the zone under a
// lone finger, the contact count, the last gestures, and with the test on the report's
// bytes in hex.
sealed record Live(double Aspect, double[][] Fingers, bool Pressed, string? Active, bool Waiting, string? Under,
    int Contacts, GestureEvent[] Events, string? Raw);

sealed record GestureEvent(int Seq, string Slot, string What, GestureAction Action);
