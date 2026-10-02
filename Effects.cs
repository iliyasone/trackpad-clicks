using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using static TrackpadClicks.Native;

namespace TrackpadClicks;

// Everything a gesture does to the system — SendInput, volume, moving windows, the log —
// runs here, one item at a time, on a thread of its own. If one of these calls hangs,
// only this thread waits; the tray notices and switches gestures off.
static class Worker
{
    static readonly BlockingCollection<(string Label, System.Action Run)> Queue = new();

    // When the current item started, 0 when idle; watched by the tray.
    public static long BusySince;
    public static volatile string At = "idle";

    public static void Post(string label, System.Action run) => Queue.Add((label, run));

    public static void Start() => new Thread(Loop) { IsBackground = true, Name = "effects" }.Start();

    static void Loop()
    {
        foreach (var (label, run) in Queue.GetConsumingEnumerable())
        {
            At = label;
            Interlocked.Exchange(ref BusySince, Environment.TickCount64);
            try { run(); }
            catch (Exception e) { Log.WriteNow($"{label}: {e.Message}"); }
            Interlocked.Exchange(ref BusySince, 0);
            At = "idle";
        }
    }
}

static class Effects
{
    // Our own SendInput calls carry this tag, though the hook already skips injected input.
    static readonly IntPtr Tag = new(0x7C11C);

    public static void Mouse(uint flags) => Worker.Post($"SendInput mouse 0x{flags:X}", () => SendMouse(flags));

    public static void Key(ushort vk) => Worker.Post($"SendInput key 0x{vk:X}", () => SendKey(vk));

    // Released without travel: the held-back press becomes the click it was.
    public static void Click(bool right) => Worker.Post("SendInput click", () =>
    {
        SendMouse(right ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_LEFTDOWN);
        SendMouse(right ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_LEFTUP);
    });

    // A selection after all: the press goes back where it happened, the cursor stays.
    public static void ReplayPress(POINT from, bool right) => Worker.Post("replay press", () =>
    {
        GetCursorPos(out var now);
        SetCursorPos(from.X, from.Y);
        SendMouse(right ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_LEFTDOWN);
        SetCursorPos(now.X, now.Y);
    });

    static void SendMouse(uint flags) => Send(new INPUT
    {
        Type = INPUT_MOUSE,
        U = new InputUnion { Mouse = new MOUSEINPUT { Flags = flags, ExtraInfo = Tag } },
    });

    static void SendKey(ushort vk)
    {
        Send(new INPUT { Type = INPUT_KEYBOARD, U = new InputUnion { Keyboard = new KEYBDINPUT { Vk = vk, Flags = KEYEVENTF_EXTENDEDKEY, ExtraInfo = Tag } } });
        Send(new INPUT { Type = INPUT_KEYBOARD, U = new InputUnion { Keyboard = new KEYBDINPUT { Vk = vk, Flags = KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, ExtraInfo = Tag } } });
    }

    static void Send(INPUT input) => SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());

    // ---- Volume slide: one volume key brings up the Windows flyout, then the level is
    // set directly through Core Audio from the travel since then.

    // Volume change per full height of the pad.
    const double VolumeGain = 0.5;
    // Wait at most this long for the flyout key to land before setting the level.
    const int KeySettleMs = 150;

    static object? _endpoint;
    static float _base;
    // Travel past either end is dropped, so reversing direction answers at once.
    static double _pin;
    static double _volumeTravel;
    static int _volumeQueued;

    public static void VolumeBegin(bool up) => Worker.Post("volume begin", () =>
    {
        _endpoint = Audio.Endpoint();
        if (_endpoint == null) return;
        float from = Audio.Get(_endpoint);
        SendKey(up ? VK_VOLUME_UP : VK_VOLUME_DOWN);
        // Setting the level before the key lands would let the key add its 2% on top.
        long start = Environment.TickCount64;
        while (Audio.Get(_endpoint) == from && Environment.TickCount64 - start < KeySettleMs) Thread.Sleep(5);
        _base = Audio.Get(_endpoint);
        _pin = 0;
    });

    // Only the latest travel matters: one queued update at a time.
    public static void VolumeTo(double travel)
    {
        Volatile.Write(ref _volumeTravel, travel);
        if (Interlocked.Exchange(ref _volumeQueued, 1) == 1) return;
        Worker.Post("volume set", () =>
        {
            Interlocked.Exchange(ref _volumeQueued, 0);
            if (_endpoint == null) return;
            double t = Volatile.Read(ref _volumeTravel) - _pin;
            double level = _base + t * VolumeGain;
            if (level > 1) { _pin += (level - 1) / VolumeGain; level = 1; }
            if (level < 0) { _pin += level / VolumeGain; level = 0; }
            Audio.Set(_endpoint, (float)level);
        });
    }

    public static void VolumeEnd() => Worker.Post("volume end", () =>
    {
        if (_endpoint != null) Log.WriteNow($"volume {Audio.Get(_endpoint):P0}");
        _endpoint = null;
    });

    // ---- Window drag.

    static IntPtr _window;
    static POINT _from;
    static RECT _windowFrom;
    static int _driftX, _driftY;
    static readonly object DragOffsetLock = new();
    static double _dragX, _dragY;
    static int _dragQueued;

    public static void DragBegin(IntPtr window, POINT from) => Worker.Post("drag begin", () =>
    {
        _window = window;
        _from = from;
        // The cursor may have drifted while the gesture was undecided: the grabbed
        // point follows it.
        GetCursorPos(out var now);
        _driftX = now.X - from.X;
        _driftY = now.Y - from.Y;
        SetForegroundWindow(window);
        GetWindowRect(window, out var rect);
        var placement = new WINDOWPLACEMENT { Length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (GetWindowPlacement(window, ref placement) && placement.ShowCmd == SW_SHOWMAXIMIZED)
        {
            // Like dragging a maximized title bar: restore, keeping the grabbed point
            // at the same share of the window's width and height.
            var normal = placement.NormalPosition;
            double shareX = (double)(from.X - rect.Left) / Math.Max(1, rect.Right - rect.Left);
            double shareY = (double)(from.Y - rect.Top) / Math.Max(1, rect.Bottom - rect.Top);
            ShowWindowAsync(window, SW_RESTORE);
            rect = new RECT
            {
                Left = from.X - (int)((normal.Right - normal.Left) * shareX),
                Top = from.Y - (int)((normal.Bottom - normal.Top) * shareY),
            };
        }
        _windowFrom = rect;
    });

    // Offset of the fingers since the drag began, in pixels. Only the latest matters.
    public static void DragTo(double x, double y)
    {
        lock (DragOffsetLock) { _dragX = x; _dragY = y; }
        if (Interlocked.Exchange(ref _dragQueued, 1) == 1) return;
        Worker.Post("drag move", () =>
        {
            Interlocked.Exchange(ref _dragQueued, 0);
            double ox, oy;
            lock (DragOffsetLock) { ox = _dragX; oy = _dragY; }
            int dx = _driftX + (int)Math.Round(ox), dy = _driftY + (int)Math.Round(oy);
            SetWindowPos(_window, IntPtr.Zero, _windowFrom.Left + dx, _windowFrom.Top + dy, 0, 0,
                SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
            SetCursorPos(_from.X + dx, _from.Y + dy);
        });
    }
}
