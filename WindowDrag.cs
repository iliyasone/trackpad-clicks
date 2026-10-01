// Physical click with 2 fingers, held, both fingers moving together = drag the window
// under the cursor, from any point of it, not just the title bar.
//
// Windows turns such a press into a left button (one finger holds the click, the
// other drags a selection) or a right click, and the hook cannot tell which the user
// means at press time. So the press is held back and the fingers decide:
//   both fingers travel        -> window drag;
//   one travels, one stays     -> the held-back press is replayed, a normal selection;
//   released without travel    -> the held-back press is replayed as a click.
// The drag ends on the pad's own release: Windows sometimes lets go of the button early.

using System.Runtime.InteropServices;
using static TrackpadClicks.Native;

namespace TrackpadClicks;

sealed partial class Watcher
{
    enum Drag { None, Pending, Moving, Passing }

    // Finger travel, as a share of the pad's width, that counts as moving (~4 mm).
    const double FingerMoves = 0.025;
    // Full pad width moves the window this many primary-screen widths.
    const double DragGain = 1.5;
    // Shell windows that keep their own clicks.
    static readonly HashSet<string> Unmovable = new() { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };

    Drag _drag;
    Pad? _dragPad;
    bool _dragPadPressed;
    bool _dragRight;          // the held-back press was a right button
    bool _upSeen, _swallowUp;
    IntPtr _dragWindow;
    POINT _dragFrom;
    RECT _windowFrom;
    long _dragStart;
    // Pending: where each finger was at the press, by contact ID.
    readonly Dictionary<uint, (double X, double Y)> _fingerStart = new();
    // Moving: average finger position last seen, finger count, travel in pixels.
    double _fingerX, _fingerY, _offX, _offY;
    int _fingerCount;

    // Returns true when the event is consumed.
    bool DragHook(int msg, MSLLHOOKSTRUCT info)
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
            var pad = down && _active == Action.None ? TwoFingerPad() : null;
            if (pad == null) return false;
            var window = MovableWindowAt(info.X, info.Y);
            if (window == IntPtr.Zero) return false;
            _drag = Drag.Pending;
            _dragPad = pad;
            _dragPadPressed = pad.Button;
            _dragRight = msg == WM_RBUTTONDOWN;
            _upSeen = _swallowUp = false;
            _dragWindow = window;
            _dragFrom = new POINT { X = info.X, Y = info.Y };
            _dragStart = Environment.TickCount64;
            _fingerStart.Clear();
            for (int i = 0; i < Math.Min(pad.YFingers, pad.Ids.Length); i++)
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

    // Raw report from the pad the press came from.
    void DragFingers(Pad pad)
    {
        if (_drag == Drag.Pending) Decide(pad);
        else if (_drag == Drag.Moving) MoveWindow(pad);
    }

    void Decide(Pad pad)
    {
        double width = Math.Max(1, pad.XMax - pad.XMin);
        int moving = 0, still = 0;
        for (int i = 0; i < Math.Min(pad.YFingers, pad.Ids.Length); i++)
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
            BeginMove(pad);
        }
        else if (moving == 1 && (still >= 1 || pad.YFingers == 1))
        {
            // A selection: hand the press back where it happened, cursor where it is now.
            _drag = Drag.Passing;
            GetCursorPos(out var now);
            SetCursorPos(_dragFrom.X, _dragFrom.Y);
            Mouse(_dragRight ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_LEFTDOWN);
            SetCursorPos(now.X, now.Y);
        }
    }

    void BeginMove(Pad pad)
    {
        _drag = Drag.Moving;
        _fingerX = pad.X;
        _fingerY = pad.Y;
        _fingerCount = pad.YFingers;
        // The cursor may have drifted while undecided: the grabbed point follows it.
        GetCursorPos(out var now);
        _offX = now.X - _dragFrom.X;
        _offY = now.Y - _dragFrom.Y;

        SetForegroundWindow(_dragWindow);
        GetWindowRect(_dragWindow, out var rect);
        var placement = new WINDOWPLACEMENT { Length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (GetWindowPlacement(_dragWindow, ref placement) && placement.ShowCmd == SW_SHOWMAXIMIZED)
        {
            // Like dragging a maximized title bar: restore, keeping the grabbed point
            // at the same share of the window's width and height.
            var normal = placement.NormalPosition;
            double shareX = (double)(_dragFrom.X - rect.Left) / Math.Max(1, rect.Right - rect.Left);
            double shareY = (double)(_dragFrom.Y - rect.Top) / Math.Max(1, rect.Bottom - rect.Top);
            ShowWindowAsync(_dragWindow, SW_RESTORE);
            rect = new RECT
            {
                Left = _dragFrom.X - (int)((normal.Right - normal.Left) * shareX),
                Top = _dragFrom.Y - (int)((normal.Bottom - normal.Top) * shareY),
            };
        }
        _windowFrom = rect;
        MoveWindow(pad);
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
        double scale = Screen.PrimaryScreen!.Bounds.Width * DragGain / Math.Max(1, pad.XMax - pad.XMin);
        _offX += (pad.X - _fingerX) * scale;
        _offY += (pad.Y - _fingerY) * scale;
        _fingerX = pad.X;
        _fingerY = pad.Y;
        int dx = (int)Math.Round(_offX), dy = (int)Math.Round(_offY);
        SetWindowPos(_dragWindow, IntPtr.Zero, _windowFrom.Left + dx, _windowFrom.Top + dy, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
        SetCursorPos(_dragFrom.X + dx, _dragFrom.Y + dy);
    }

    void EndDrag(string by)
    {
        // Released without travel: the held-back press becomes the click it was.
        if (_drag == Drag.Pending)
        {
            Mouse(_dragRight ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_LEFTDOWN);
            Mouse(_dragRight ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_LEFTUP);
        }
        Log.Write($"two-finger {(_dragRight ? "right" : "left")} press -> {_drag switch { Drag.Moving => "window drag", Drag.Passing => "selection", _ => "click" }}"
                  + $", {Environment.TickCount64 - _dragStart} ms, ended by {by}");
        // In Passing the real button up went through to the app already.
        _swallowUp = _drag != Drag.Passing && !_upSeen;
        _drag = Drag.None;
        _dragPad = null;
        _dragWindow = IntPtr.Zero;
    }

    // A pad with exactly 2 fingers on it right now, if any.
    Pad? TwoFingerPad()
    {
        long now = Environment.TickCount64;
        foreach (var pad in _pads.Values)
            if (pad != null && pad.Contacts == 2 && now - pad.LastReport < FreshMs) return pad;
        return null;
    }

    static IntPtr MovableWindowAt(int x, int y)
    {
        var window = GetAncestor(WindowFromPoint(new POINT { X = x, Y = y }), GA_ROOT);
        if (window == IntPtr.Zero) return IntPtr.Zero;
        var name = new System.Text.StringBuilder(64);
        GetClassName(window, name, name.Capacity);
        return Unmovable.Contains(name.ToString()) ? IntPtr.Zero : window;
    }
}
