// What each physical click means. All of it runs under Sync, called from the raw input
// thread (finger reports) and the mouse hook (button events), and only changes state
// in memory; anything that touches the system goes through Effects.
//
// Fingers resting on the pad before the click are told apart by when each landed;
// see ClickFingers. With two fingers resting, a click by a third goes to Windows.
//
// Click with 3 fingers = middle button.
// Click with 4+ fingers, by where the fingers are on the pad:
//   top third    = volume up a step; held, it repeats like a held key;
//   bottom third = the same, down;
//   middle third = release in place for media play/pause, or hold and slide
//                  up/down to set the volume continuously as the fingers travel.
// Click with 2 fingers: Windows makes it a left press (one finger holds the click, the
// other drags a selection) or a right click, and which one the user means is not known
// at press time. So the press is held back and the fingers decide:
//   both fingers travel        -> drag the window under the cursor, from any point of it;
//   one travels, one stays     -> the press is replayed, a normal selection;
//   released without travel    -> the press is replayed as the click it was.
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
    // Less travel than this (share of the pad's height) on a four-finger click is play/pause.
    const double StillTravel = 0.02;
    // Held top/bottom click: first repeat after this, then one step per RepeatMs.
    const int RepeatDelayMs = 350, RepeatMs = 60;
    // Finger travel, as a share of the pad's width, that counts as moving (~4 mm).
    const double FingerMoves = 0.025;
    // Full pad width moves the window this many primary-screen widths.
    const double DragGain = 1.5;
    // Shell windows that keep their own clicks.
    static readonly HashSet<string> Unmovable = new() { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };

    enum Mode { None, Middle, FourFinger }
    Mode _mode;
    Pad? _modePad;
    bool _swallowing;

    // Four-finger hold. Travel is in shares of the pad's height, up is positive.
    double _anchorY;
    int _anchorFingers;
    double _travel;
    bool _sliding;
    ushort _repeatKey;
    readonly System.Threading.Timer _repeat;

    enum Drag { None, Pending, Moving, Passing }
    Drag _drag;
    Pad? _dragPad;
    bool _dragPadPressed;
    bool _dragRight;
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
    }

    // Drop whatever is in progress; for switching off.
    public void Reset()
    {
        _repeat.Change(Timeout.Infinite, Timeout.Infinite);
        if (_mode == Mode.Middle) Effects.Mouse(MOUSEEVENTF_MIDDLEUP);
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
        if (button && !pad.Button && pad.Contacts >= 3)
        {
            int fingers = ClickFingers(pad);
            if (fingers >= 3) Start(pad, fingers);
            else if (_mode == Mode.None)
                Log.Write($"{pad.Contacts} fingers, the last one added alone (landed {pad.LandingsText} ms before the last), left to Windows");
        }
        else if (!button && pad.Button && pad == _modePad)
        {
            End();
        }
        pad.Button = button;

        if (_mode == Mode.FourFinger && _repeatKey == 0 && pad == _modePad) Slide(pad);

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
            var pad = _modePad ?? FreshPad(p => ClickFingers(p) >= 3);
            if (pad != null)
            {
                Start(pad, ClickFingers(pad));
                _swallowing = true;
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

    // ---- Three and four fingers.

    // How many fingers a click is made with, by how they landed. The last fingers to
    // land within TogetherMs of each other are the fresh group:
    //   3+ fresh             -> that many; a finger resting before (a thumb) is left out;
    //   2 fresh              -> every finger: one finger pointing, two added to click;
    //   1 fresh, soon after  -> every finger: a slow but single motion;
    //   1 fresh, later       -> 1: the others rest, the click is that finger's.
    static int ClickFingers(Pad pad)
    {
        var before = pad.Landings(); // longest first, the last finger at 0
        int fresh = before.Count(ms => ms <= TogetherMs);
        if (fresh >= 3) return fresh;
        if (fresh == 2) return before.Length;
        return before.Length >= 2 && before[^2] <= SameMotionMs ? before.Length : 1;
    }

    void Start(Pad pad, int fingers)
    {
        if (_mode != Mode.None) return;
        _modePad = pad;
        string where = "";
        if (fingers >= 4)
        {
            // Play/pause waits for the release: a slide in between means volume instead.
            _mode = Mode.FourFinger;
            _anchorY = pad.Y;
            _anchorFingers = pad.YFingers;
            _travel = 0;
            _sliding = false;
            // Y grows downward; with no finger positions fall back to the middle zone.
            double at = pad.YFingers > 0 ? (pad.Y - pad.YMin) / Math.Max(1, pad.YMax - pad.YMin) : 0.5;
            _repeatKey = at < 1.0 / 3 ? VK_VOLUME_UP : at > 2.0 / 3 ? VK_VOLUME_DOWN : (ushort)0;
            if (_repeatKey != 0)
            {
                Effects.Key(_repeatKey);
                _repeat.Change(RepeatDelayMs, RepeatMs);
                where = _repeatKey == VK_VOLUME_UP ? " top" : " bottom";
            }
        }
        else
        {
            _mode = Mode.Middle;
            Effects.Mouse(MOUSEEVENTF_MIDDLEDOWN);
        }
        Log.Write($"{_mode} ({fingers} fingers, landed {pad.LandingsText} ms before the last){where}");
    }

    void End()
    {
        if (_mode == Mode.Middle) Effects.Mouse(MOUSEEVENTF_MIDDLEUP);
        if (_mode == Mode.FourFinger)
        {
            _repeat.Change(Timeout.Infinite, Timeout.Infinite);
            if (_repeatKey != 0) _repeatKey = 0;
            else if (!_sliding)
            {
                Effects.Key(VK_MEDIA_PLAY_PAUSE);
                Log.Write("play/pause");
            }
            else Effects.VolumeEnd();
        }
        _mode = Mode.None;
        _modePad = null;
    }

    void Slide(Pad pad)
    {
        if (pad.YFingers == 0) return;
        // A finger landing or lifting jumps the average; measure afresh from here.
        if (pad.YFingers != _anchorFingers)
        {
            _anchorY = pad.Y;
            _anchorFingers = pad.YFingers;
            return;
        }
        // Y grows downward: fingers moving up give positive travel.
        _travel += (_anchorY - pad.Y) / Math.Max(1, pad.YMax - pad.YMin);
        _anchorY = pad.Y;
        if (!_sliding)
        {
            if (Math.Abs(_travel) < StillTravel) return;
            _sliding = true;
            Effects.VolumeBegin(_travel > 0);
            _travel = 0;
            return;
        }
        Effects.VolumeTo(_travel);
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
        if (_drag == Drag.Pending) Effects.Click(_dragRight);
        Log.Write($"two-finger {(_dragRight ? "right" : "left")} press -> {_drag switch { Drag.Moving => "window drag", Drag.Passing => "selection", _ => "click" }}"
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
