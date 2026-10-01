// Two-finger click held and moved = drag the window under the cursor, from any
// point of it, not just the title bar. Released without moving = the ordinary
// right click, sent on release.
//
// Windows turns a two-finger click into a right button press; the hook swallows it
// and waits. Moving past DragStartPx makes it a window drag. With the button held,
// Windows may not move the cursor at all, so the fingers' own travel from the raw
// reports drives the drag (and the cursor); cursor moves, if Windows does send them,
// drive it instead.

using System.Runtime.InteropServices;
using static TrackpadClicks.Native;

namespace TrackpadClicks;

sealed partial class Watcher
{
    enum Drag { None, Pending, Moving }

    const int DragStartPx = 8;
    // Full pad width moves the window this many primary-screen widths.
    const double DragGain = 1.5;
    // Shell windows that must keep their own right click.
    static readonly HashSet<string> Unmovable = new() { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };

    Drag _drag;
    bool _dragByFingers;
    Pad? _dragPad;
    double _fingerX, _fingerY, _offX, _offY;
    int _fingerCount;
    IntPtr _dragWindow;
    POINT _dragFrom;
    RECT _windowFrom;
    int _dragMoves, _dragWheels;
    long _dragStart;
    bool _rightUpSeen, _swallowRightUp, _dragPadPressed;

    // Returns true when the event is consumed.
    bool DragHook(int msg, MSLLHOOKSTRUCT info)
    {
        if (_drag == Drag.None)
        {
            // The drag ended on the pad's own release before Windows sent its button up.
            if (msg == WM_RBUTTONUP && _swallowRightUp)
            {
                _swallowRightUp = false;
                return true;
            }
            var pad = msg == WM_RBUTTONDOWN && _active == Action.None ? TwoFingerPad() : null;
            if (pad == null) return false;
            var window = MovableWindowAt(info.X, info.Y);
            if (window == IntPtr.Zero) return false;
            _drag = Drag.Pending;
            _dragByFingers = false;
            _dragPad = pad;
            _fingerX = pad.X;
            _fingerY = pad.Y;
            _fingerCount = pad.YFingers;
            _offX = _offY = 0;
            _dragWindow = window;
            _dragFrom = new POINT { X = info.X, Y = info.Y };
            _dragMoves = _dragWheels = 0;
            _dragStart = Environment.TickCount64;
            _rightUpSeen = _swallowRightUp = false;
            _dragPadPressed = pad.Button;
            return true;
        }

        switch (msg)
        {
            case WM_MOUSEMOVE:
                if (_dragByFingers) return false;
                _dragMoves++;
                Follow(info.X, info.Y);
                return false;
            case WM_MOUSEWHEEL or WM_MOUSEHWHEEL:
                // Two fingers moving may read as a scroll; it must not reach the app mid-drag.
                _dragWheels++;
                return true;
            case WM_RBUTTONDOWN:
                return true;
            case WM_RBUTTONUP:
                // Windows may let go of the right button while the pad is still pressed;
                // the pad's own release ends the drag then.
                _rightUpSeen = true;
                if (_dragPadPressed && _dragPad is { Button: true }) return true;
                EndDrag("button up");
                return true;
        }
        return false;
    }

    void EndDrag(string by)
    {
        if (_drag == Drag.Pending)
        {
            Mouse(MOUSEEVENTF_RIGHTDOWN);
            Mouse(MOUSEEVENTF_RIGHTUP);
        }
        Log.Write($"two-finger {(_drag == Drag.Moving ? "drag" : "right click")}{(_dragByFingers ? " by fingers" : "")}"
                  + $" {Environment.TickCount64 - _dragStart} ms, ended by {by}, right up {(_rightUpSeen ? "early" : "pending")}, {_dragMoves} moves, {_dragWheels} wheels");
        _swallowRightUp = !_rightUpSeen;
        _drag = Drag.None;
        _dragPad = null;
        _dragWindow = IntPtr.Zero;
    }

    // Raw report from the dragging pad: used unless Windows is moving the cursor itself.
    void DragFingers(Pad pad)
    {
        if (_dragMoves > 0 && !_dragByFingers) return;
        if (pad.YFingers == 0) return;
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
        int x = _dragFrom.X + (int)Math.Round(_offX), y = _dragFrom.Y + (int)Math.Round(_offY);
        if (_drag == Drag.Pending && Math.Abs(_offX) + Math.Abs(_offY) >= DragStartPx) _dragByFingers = true;
        if (!_dragByFingers) return;
        Follow(x, y);
        SetCursorPos(x, y);
    }

    // The grabbed point is now at (x, y).
    void Follow(int x, int y)
    {
        if (_drag == Drag.Pending && Math.Abs(x - _dragFrom.X) + Math.Abs(y - _dragFrom.Y) >= DragStartPx)
            BeginMove();
        if (_drag == Drag.Moving)
            SetWindowPos(_dragWindow, IntPtr.Zero,
                _windowFrom.Left + x - _dragFrom.X, _windowFrom.Top + y - _dragFrom.Y, 0, 0,
                SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
    }

    void BeginMove()
    {
        _drag = Drag.Moving;
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
