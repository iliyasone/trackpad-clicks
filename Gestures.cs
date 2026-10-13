// What each physical click means. All of it runs under Sync, called from the raw input
// thread (finger reports) and the mouse hook (button events), and only changes state
// in memory; anything that touches the system goes through Effects.
//
// Fingers resting on the pad before the click are told apart by when each landed and
// whether it moved since; see ClickFingers. With two fingers resting, a click by a
// third goes to Windows.
//
// Click with 3 fingers = middle button. Four or more fingers: nothing, left to Windows.
// Click with 1 finger (a thumb, say) at an edge of the pad, by height:
//                  left edge         right edge
//   top third      next track        volume up a step; held, it repeats like a held key
//   middle third   media play/pause  middle button (record)
//   bottom third   previous track    volume down, like volume up
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
    // An older finger that moved within this before the last landing was pointing,
    // not resting, and is counted in the click.
    const long PointingMs = 1500;
    // The edge strips with the media clicks, each as a share of the pad's width (~32 mm).
    const double EdgeWidth = 0.2;
    // EdgeKey's answer for the right edge's middle: the middle button, not a key.
    const ushort EdgeMiddle = 0;
    // Held top/bottom click: first repeat after this, then one step per RepeatMs.
    const int RepeatDelayMs = 350, RepeatMs = 60;
    // Finger travel, as a share of the pad's width, that counts as moving (~4 mm).
    const double FingerMoves = 0.025;
    // Full pad width moves the window this many primary-screen widths.
    const double DragGain = 1.5;
    // Shell windows that keep their own clicks.
    static readonly HashSet<string> Unmovable = new() { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };

    enum Mode { None, Middle, Edge }
    Mode _mode;
    Pad? _modePad;
    bool _swallowing;

    // Edge click: the volume key held, or 0 for any other.
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
        if (button && !pad.Button && EdgeKey(pad) is ushort key)
        {
            StartEdge(pad, key);
        }
        else if (button && !pad.Button && pad.Contacts >= 3)
        {
            int fingers = ClickFingers(pad);
            if (fingers == 3) StartMiddle(pad);
            else if (_mode == Mode.None)
                Log.Write($"{pad.Contacts} fingers, counted {fingers} (landed {pad.LandingsText} ms before the last), left to Windows");
        }
        else if (!button && pad.Button && pad == _modePad)
        {
            End();
        }
        pad.Button = button;

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
            if (_modePad == null)
            {
                if (FreshPad(p => EdgeKey(p) != null) is Pad edge) StartEdge(edge, EdgeKey(edge)!.Value);
                else if (FreshPad(p => p.Contacts >= 3 && ClickFingers(p) == 3) is Pad pad) StartMiddle(pad);
            }
            if (_modePad != null)
            {
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

    // ---- Three fingers.

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

    // The media key for a one-finger click at an edge (EdgeMiddle for the middle
    // button), or null elsewhere.
    static ushort? EdgeKey(Pad pad)
    {
        if (pad.Contacts != 1 || pad.YFingers != 1) return null;
        double x = (pad.Xs[0] - pad.XMin) / Math.Max(1, pad.XMax - pad.XMin);
        bool left = x <= EdgeWidth;
        if (!left && x < 1 - EdgeWidth) return null;
        // Y grows downward.
        double y = (pad.Ys[0] - pad.YMin) / Math.Max(1, pad.YMax - pad.YMin);
        int third = y < 1.0 / 3 ? 0 : y > 2.0 / 3 ? 2 : 1;
        return left
            ? third switch { 0 => VK_MEDIA_NEXT_TRACK, 1 => VK_MEDIA_PLAY_PAUSE, _ => VK_MEDIA_PREV_TRACK }
            : third switch { 0 => VK_VOLUME_UP, 1 => EdgeMiddle, _ => VK_VOLUME_DOWN };
    }

    void StartMiddle(Pad pad)
    {
        if (_mode != Mode.None) return;
        _mode = Mode.Middle;
        _modePad = pad;
        Effects.Mouse(MOUSEEVENTF_MIDDLEDOWN);
        Log.Write($"middle (3 fingers, landed {pad.LandingsText} ms before the last)");
    }

    // A track key or play/pause goes at once; a volume step repeats while held; the
    // middle button is held while the pad is.
    void StartEdge(Pad pad, ushort key)
    {
        if (_mode != Mode.None) return;
        _modePad = pad;
        if (key == EdgeMiddle)
        {
            _mode = Mode.Middle;
            Effects.Mouse(MOUSEEVENTF_MIDDLEDOWN);
            Log.Write("edge: middle");
            return;
        }
        _mode = Mode.Edge;
        Effects.Key(key);
        if (key is VK_VOLUME_UP or VK_VOLUME_DOWN)
        {
            _repeatKey = key;
            _repeat.Change(RepeatDelayMs, RepeatMs);
        }
        Log.Write(key switch
        {
            VK_VOLUME_UP => "edge: volume up",
            VK_VOLUME_DOWN => "edge: volume down",
            VK_MEDIA_NEXT_TRACK => "edge: next track",
            VK_MEDIA_PREV_TRACK => "edge: previous track",
            _ => "edge: play/pause",
        });
    }

    void End()
    {
        if (_mode == Mode.Middle) Effects.Mouse(MOUSEEVENTF_MIDDLEUP);
        _repeat.Change(Timeout.Infinite, Timeout.Infinite);
        _repeatKey = 0;
        _mode = Mode.None;
        _modePad = null;
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
