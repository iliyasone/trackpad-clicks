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

    public static void Mouse(uint flags, uint data = 0) => Worker.Post($"SendInput mouse 0x{flags:X}", () => SendMouse(flags, data));

    public static void Key(ushort vk) => Worker.Post($"SendInput key 0x{vk:X}", () => SendKey(vk));

    // Keys pressed in T3 Code: its window is brought forward first. A background app may
    // not take the foreground, but one that just sent input may: a tap of a key no app uses
    // first (Alt would put the app in front into its menu).
    public static void T3Keys(string label, params ushort[] chord) => Worker.Post($"T3 Code: {label}", () =>
    {
        var window = System.Diagnostics.Process.GetProcesses()
            .Where(p => p.ProcessName.StartsWith("T3 Code", StringComparison.OrdinalIgnoreCase) && p.MainWindowHandle != IntPtr.Zero)
            .Select(p => p.MainWindowHandle).FirstOrDefault();
        if (window == IntPtr.Zero)
        {
            Log.WriteNow($"T3 Code: {label}: no T3 Code window");
            return;
        }
        if (GetForegroundWindow() != window)
        {
            SendKeyEvent(VK_NONE, false);
            SendKeyEvent(VK_NONE, true);
            if (IsIconic(window)) ShowWindowAsync(window, SW_RESTORE);
            SetForegroundWindow(window);
            Thread.Sleep(80);
            // Refused: the keys would land in whatever app is in front.
            if (GetForegroundWindow() != window)
            {
                Log.WriteNow($"T3 Code: {label}: its window didn't come forward");
                return;
            }
        }
        foreach (var vk in chord) SendKeyEvent(vk, false);
        foreach (var vk in chord.Reverse()) SendKeyEvent(vk, true);
    });

    static void SendKeyEvent(ushort vk, bool up) => Send(new INPUT
    {
        Type = INPUT_KEYBOARD,
        U = new InputUnion { Keyboard = new KEYBDINPUT { Vk = vk, Flags = up ? KEYEVENTF_KEYUP : 0, ExtraInfo = Tag } },
    });

    // Play/pause or a track key for one app's media session (see Media.cs).
    public static void MediaKey(string app, GestureAction action) => Worker.Post($"media {action} to {app}", () =>
    {
        if (!Media.Send(app, action)) Log.WriteNow($"media {action}: nothing from \"{app}\" is playing");
    });

    // Play/pause for every app but the music one (see Media.cs).
    public static void OtherPlayPause(string except) => Worker.Post($"media play/pause, not {except}", () =>
    {
        if (!Media.ToggleOther(except)) Log.WriteNow($"media play/pause: nothing but \"{except}\" has played");
    });

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

    static void SendMouse(uint flags, uint data = 0) => Send(new INPUT
    {
        Type = INPUT_MOUSE,
        U = new InputUnion { Mouse = new MOUSEINPUT { Flags = flags, MouseData = data, ExtraInfo = Tag } },
    });

    static void SendKey(ushort vk)
    {
        Send(new INPUT { Type = INPUT_KEYBOARD, U = new InputUnion { Keyboard = new KEYBDINPUT { Vk = vk, Flags = KEYEVENTF_EXTENDEDKEY, ExtraInfo = Tag } } });
        Send(new INPUT { Type = INPUT_KEYBOARD, U = new InputUnion { Keyboard = new KEYBDINPUT { Vk = vk, Flags = KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, ExtraInfo = Tag } } });
    }

    static void Send(INPUT input) => SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());

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

    // ---- Haptics: a click under the finger, through the Magic Trackpad driver's
    // \\.\AmtPtpHapticDeviceUm (IOCTL_PTPFILTER_ACTUATE, from the change proposed in
    // vitoplantamura/MagicTrackpad2ForWindows#63). Without that driver, nothing happens.

    const uint IoctlActuate = 0x0022A008;
    static Microsoft.Win32.SafeHandles.SafeFileHandle? _haptic;
    static long _hapticRetryAt;

    // Strength in the driver's FeedbackClick encoding (0x060617: medium).
    public static void Haptic(uint strength) => Worker.Post("haptic click", () =>
    {
        long now = Environment.TickCount64;
        if (_haptic == null || _haptic.IsInvalid)
        {
            if (now < _hapticRetryAt) return;
            _haptic = CreateFile(@"\\.\AmtPtpHapticDeviceUm", GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (_haptic.IsInvalid)
            {
                // Not this driver: look again in a minute, not on every click.
                _hapticRetryAt = now + 60_000;
                return;
            }
        }
        if (!DeviceIoControl(_haptic, IoctlActuate, ref strength, 4, IntPtr.Zero, 0, out _, IntPtr.Zero))
        {
            Log.WriteNow($"haptic click failed {Marshal.GetLastWin32Error()}");
            _haptic.Dispose();
            _haptic = null;
        }
    });
}
