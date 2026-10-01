// Physical click with 3 fingers = middle button.
// Physical click with 4+ fingers, by where the fingers are on the pad:
//   top third    = volume up a step; held, it repeats like a held key;
//   bottom third = the same, down;
//   middle third = release in place for media play/pause, or hold and slide
//                  up/down to set the volume continuously as the fingers travel.
// Physical click with 2 fingers, held and moved = drag the window (WindowDrag.cs).
// Taps are left to Windows settings (turned off there); this only remaps real clicks.
//
// Finger count, finger positions and button state come from the precision touchpad's
// raw HID reports. The left/right click Windows synthesizes for the same press is
// swallowed by a low-level mouse hook. Both run on the one UI thread, so no locking.

using System.Runtime.InteropServices;
using static TrackpadClicks.Native;

namespace TrackpadClicks;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, @"Local\TrackpadClicks", out bool first);
        if (!first) return;
        // Hook coordinates and window rects must both be physical pixels.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Log.Write("start");
        var watcher = new Watcher();
        Application.Run();
        GC.KeepAlive(watcher);
    }
}

enum Action { None, Middle, FourFinger }

// One touchpad. The built-in ASUS pad streams reports too (here: constantly "pressed",
// 0 fingers), so every touchpad keeps its own state.
sealed class Pad
{
    public IntPtr Preparsed;
    public ushort LinkCollections;
    public int XMin, XMax, YMin, YMax;

    public int Contacts;
    public long LastReport;
    public bool Button;
    // Average position of the fingers touching, and how many fingers it covers.
    public double X, Y;
    public int YFingers;
}

sealed partial class Watcher : NativeWindow
{
    // Our own SendInput calls carry this tag, though the hook already skips injected input.
    static readonly IntPtr Tag = new(0x7C11C);
    const long FreshMs = 250;
    // Volume change per full height of the pad.
    const double VolumeGain = 0.5;
    // Less travel than this (share of the pad's height) on a four-finger click is play/pause.
    const double StillTravel = 0.02;
    // After the volume key that brings up the flyout, wait at most this long for it to land.
    const long KeySettleMs = 150;
    // Held top/bottom click: first repeat after this, then one step per RepeatMs.
    const int RepeatDelayMs = 350, RepeatMs = 60;

    readonly HookProc _hookProc;
    readonly IntPtr _hook;
    readonly Dictionary<IntPtr, Pad?> _pads = new();
    readonly ushort[] _usages = new ushort[32];
    readonly bool _debug = File.Exists(@"C:\TrackpadClicks\debug");

    Action _active;
    Pad? _activePad;
    bool _swallowing;
    bool _loggedParseError;

    // Four-finger hold. Travel is in shares of the pad's height, up is positive.
    double _anchorY;
    int _anchorFingers;
    double _travel;
    // Volume mode starts once travel leaves the dead zone: one volume key shows the
    // Windows flyout, then the level is set directly from the travel since then.
    bool _sliding;
    object? _endpoint;
    float _keyFrom, _base;
    long _keySent;
    bool _keyLanded;

    // Top/bottom four-finger click: the volume key it repeats while held.
    readonly System.Windows.Forms.Timer _repeat = new();
    ushort _repeatKey;

    public Watcher()
    {
        CreateHandle(new CreateParams());
        var dev = new[] { new RAWINPUTDEVICE { UsagePage = 0x0D, Usage = 0x05, Flags = RIDEV_INPUTSINK, Target = Handle } };
        if (!RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            Log.Write($"RegisterRawInputDevices failed {Marshal.GetLastWin32Error()}");
        _hookProc = Hook;
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _hookProc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) Log.Write($"SetWindowsHookEx failed {Marshal.GetLastWin32Error()}");
        _repeat.Tick += (_, _) =>
        {
            _repeat.Interval = RepeatMs;
            Key(_repeatKey);
        };
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_INPUT) OnRawInput(m.LParam);
        base.WndProc(ref m);
    }

    void OnRawInput(IntPtr handle)
    {
        uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        uint size = 0;
        GetRawInputData(handle, RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (size == 0) return;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(handle, RID_INPUT, buf, ref size, headerSize) != size) return;
            var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buf);
            if (header.Type != RIM_TYPEHID) return;
            var pad = GetPad(header.Device);
            if (pad == null) return;

            uint reportSize = (uint)Marshal.ReadInt32(buf, (int)headerSize);
            uint count = (uint)Marshal.ReadInt32(buf, (int)headerSize + 4);
            IntPtr report = buf + (int)headerSize + 8;
            for (uint i = 0; i < count; i++, report += (int)reportSize)
                OnReport(header.Device, pad, report, reportSize);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    void OnReport(IntPtr device, Pad pad, IntPtr report, uint length)
    {
        int status = HidP_GetUsageValue(HidP_Input, 0x0D, 0, 0x54, out uint contacts, pad.Preparsed, report, length);
        if (status != HIDP_STATUS_SUCCESS)
        {
            if (!_loggedParseError) Log.Write($"contact count parse failed 0x{status:X}");
            _loggedParseError = true;
            return;
        }
        uint n = (uint)_usages.Length;
        bool button = HidP_GetUsages(HidP_Input, 0x09, 0, _usages, ref n, pad.Preparsed, report, length) == HIDP_STATUS_SUCCESS
                      && _usages.Take((int)n).Contains((ushort)1);

        pad.Contacts = (int)contacts;
        pad.LastReport = Environment.TickCount64;
        if (pad.Contacts > 0) ReadFingers(pad, report, length);
        else pad.YFingers = 0;

        if (button != pad.Button && _debug)
        {
            var bytes = new byte[length];
            Marshal.Copy(report, bytes, 0, (int)length);
            Log.Write($"dev {device} {(button ? "down" : "up")} {contacts} fingers y {pad.Y:F0}/{pad.YFingers} {Convert.ToHexString(bytes)}");
        }
        if (button && !pad.Button)
        {
            if (pad.Contacts >= 3) Start(pad, pad.Contacts);
        }
        else if (!button && pad.Button && pad == _activePad)
        {
            End();
        }
        pad.Button = button;

        if (_active == Action.FourFinger && _repeatKey == 0 && pad == _activePad) Slide(pad);
        if (_drag != Drag.None && pad == _dragPad) DragFingers(pad);
    }

    // Each finger is its own link collection holding a tip switch and X/Y. The Magic
    // Trackpad driver fills only the first `Contacts` of them; the rest hold leftover
    // memory, sometimes with the tip switch set, so only those first slots are read.
    void ReadFingers(Pad pad, IntPtr report, uint length)
    {
        double sumX = 0, sumY = 0;
        int fingers = 0;
        int slots = Math.Min(pad.Contacts, pad.LinkCollections - 1);
        for (ushort link = 1; link <= slots; link++)
        {
            if (HidP_GetUsageValue(HidP_Input, 0x01, link, 0x31, out uint y, pad.Preparsed, report, length) != HIDP_STATUS_SUCCESS
                || y < pad.YMin || y > pad.YMax
                || HidP_GetUsageValue(HidP_Input, 0x01, link, 0x30, out uint x, pad.Preparsed, report, length) != HIDP_STATUS_SUCCESS
                || x < pad.XMin || x > pad.XMax)
                continue;
            uint n = (uint)_usages.Length;
            if (HidP_GetUsages(HidP_Input, 0x0D, link, _usages, ref n, pad.Preparsed, report, length) != HIDP_STATUS_SUCCESS
                || !_usages.Take((int)n).Contains((ushort)0x42))
                continue;
            sumX += x;
            sumY += y;
            fingers++;
        }
        pad.X = fingers > 0 ? sumX / fingers : 0;
        pad.Y = fingers > 0 ? sumY / fingers : 0;
        pad.YFingers = fingers;
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
        double height = Math.Max(1, pad.YMax - pad.YMin);
        // Y grows downward: fingers moving up give positive travel.
        _travel += (_anchorY - pad.Y) / height;
        _anchorY = pad.Y;

        if (!_sliding)
        {
            if (Math.Abs(_travel) < StillTravel) return;
            _endpoint = Audio.Endpoint();
            if (_endpoint == null) return;
            _sliding = true;
            _keyFrom = Audio.Get(_endpoint);
            _keyLanded = false;
            _keySent = Environment.TickCount64;
            Key(_travel > 0 ? VK_VOLUME_UP : VK_VOLUME_DOWN);
            _travel = 0;
            return;
        }
        if (_endpoint == null) return;
        if (!_keyLanded)
        {
            // Setting the level before the key lands would let the key add its 2% on top.
            float now = Audio.Get(_endpoint);
            if (now == _keyFrom && Environment.TickCount64 - _keySent < KeySettleMs) return;
            _keyLanded = true;
            _base = now;
        }
        double level = _base + _travel * VolumeGain;
        // Pin travel at the ends so reversing direction answers at once.
        if (level > 1) { level = 1; _travel = (1 - _base) / VolumeGain; }
        if (level < 0) { level = 0; _travel = -_base / VolumeGain; }
        Audio.Set(_endpoint, (float)level);
    }

    // A pad with 3+ fingers on it right now, if any.
    Pad? MultiFingerPad()
    {
        long now = Environment.TickCount64;
        foreach (var pad in _pads.Values)
            if (pad != null && pad.Contacts >= 3 && now - pad.LastReport < FreshMs) return pad;
        return null;
    }

    IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            int msg = (int)wParam;
            if ((info.Flags & LLMHF_INJECTED) == 0)
            {
                if (_active == Action.None && DragHook(msg, info)) return 1;
                if (msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN)
                {
                    var pad = _activePad ?? MultiFingerPad();
                    if (pad != null)
                    {
                        Start(pad, pad.Contacts);
                        _swallowing = true;
                        return 1;
                    }
                }
                else if (msg is WM_LBUTTONUP or WM_RBUTTONUP && _swallowing)
                {
                    _swallowing = false;
                    End();
                    return 1;
                }
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    void Start(Pad pad, int fingers)
    {
        if (_active != Action.None) return;
        _activePad = pad;
        if (fingers >= 4)
        {
            // Play/pause waits for the release: a slide in between means volume instead.
            _active = Action.FourFinger;
            _anchorY = pad.Y;
            _anchorFingers = pad.YFingers;
            _travel = 0;
            _sliding = false;
            // Y grows downward; with no finger positions fall back to the middle zone.
            double at = pad.YFingers > 0 ? (pad.Y - pad.YMin) / Math.Max(1, pad.YMax - pad.YMin) : 0.5;
            _repeatKey = at < 1.0 / 3 ? VK_VOLUME_UP : at > 2.0 / 3 ? VK_VOLUME_DOWN : (ushort)0;
            if (_repeatKey != 0)
            {
                Key(_repeatKey);
                _repeat.Interval = RepeatDelayMs;
                _repeat.Start();
            }
        }
        else
        {
            _active = Action.Middle;
            Mouse(MOUSEEVENTF_MIDDLEDOWN);
        }
        Log.Write($"{_active} ({fingers} fingers){(_repeatKey == VK_VOLUME_UP ? " top" : _repeatKey == VK_VOLUME_DOWN ? " bottom" : "")}");
    }

    void End()
    {
        if (_active == Action.Middle) Mouse(MOUSEEVENTF_MIDDLEUP);
        if (_active == Action.FourFinger)
        {
            _repeat.Stop();
            if (_repeatKey != 0) _repeatKey = 0;
            else if (!_sliding)
            {
                Key(VK_MEDIA_PLAY_PAUSE);
                Log.Write("play/pause");
            }
            else if (_endpoint != null) Log.Write($"volume {Audio.Get(_endpoint):P0}");
            _endpoint = null;
        }
        _active = Action.None;
        _activePad = null;
    }

    static void Mouse(uint flags) => Send(new INPUT
    {
        Type = INPUT_MOUSE,
        U = new InputUnion { Mouse = new MOUSEINPUT { Flags = flags, ExtraInfo = Tag } },
    });

    static void Key(ushort vk)
    {
        Send(new INPUT { Type = INPUT_KEYBOARD, U = new InputUnion { Keyboard = new KEYBDINPUT { Vk = vk, Flags = KEYEVENTF_EXTENDEDKEY, ExtraInfo = Tag } } });
        Send(new INPUT { Type = INPUT_KEYBOARD, U = new InputUnion { Keyboard = new KEYBDINPUT { Vk = vk, Flags = KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, ExtraInfo = Tag } } });
    }

    static void Send(INPUT input) => SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());

    Pad? GetPad(IntPtr device)
    {
        if (_pads.TryGetValue(device, out var pad)) return pad;
        pad = null;
        uint size = 0;
        GetRawInputDeviceInfo(device, RIDI_PREPARSEDDATA, IntPtr.Zero, ref size);
        if (size > 0)
        {
            IntPtr p = Marshal.AllocHGlobal((int)size);
            if ((int)GetRawInputDeviceInfo(device, RIDI_PREPARSEDDATA, p, ref size) > 0) pad = Describe(p);
            else Marshal.FreeHGlobal(p);
        }
        _pads[device] = pad;
        Log.Write($"touchpad {DeviceName(device)}, {pad?.LinkCollections} link collections, x {pad?.XMin}..{pad?.XMax}, y {pad?.YMin}..{pad?.YMax}");
        return pad;
    }

    static Pad Describe(IntPtr preparsed)
    {
        var pad = new Pad { Preparsed = preparsed };
        // HIDP_CAPS: NumberLinkCollectionNodes sits after 5 lengths and 17 reserved USHORTs.
        var caps = new byte[64];
        if (HidP_GetCaps(preparsed, caps) == HIDP_STATUS_SUCCESS)
            pad.LinkCollections = BitConverter.ToUInt16(caps, 44);
        // HIDP_VALUE_CAPS is 72 bytes, LogicalMin/LogicalMax at 40/44.
        var valueCaps = new byte[72 * 16];
        ushort count = 16;
        if (HidP_GetSpecificValueCaps(HidP_Input, 0x01, 0, 0x30, valueCaps, ref count, preparsed) == HIDP_STATUS_SUCCESS && count > 0)
        {
            pad.XMin = BitConverter.ToInt32(valueCaps, 40);
            pad.XMax = BitConverter.ToInt32(valueCaps, 44);
        }
        count = 16;
        if (HidP_GetSpecificValueCaps(HidP_Input, 0x01, 0, 0x31, valueCaps, ref count, preparsed) == HIDP_STATUS_SUCCESS && count > 0)
        {
            pad.YMin = BitConverter.ToInt32(valueCaps, 40);
            pad.YMax = BitConverter.ToInt32(valueCaps, 44);
        }
        return pad;
    }

    static string DeviceName(IntPtr device)
    {
        uint chars = 0;
        GetRawInputDeviceInfo(device, RIDI_DEVICENAME, IntPtr.Zero, ref chars);
        IntPtr buf = Marshal.AllocHGlobal((int)chars * 2);
        try
        {
            GetRawInputDeviceInfo(device, RIDI_DEVICENAME, buf, ref chars);
            return Marshal.PtrToStringUni(buf) ?? "?";
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}

static class Log
{
    static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrackpadClicks", "log.txt");

    public static void Write(string line)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            if (File.Exists(Path) && new FileInfo(Path).Length > 1_000_000) File.Delete(Path);
            File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}\n");
        }
        catch { }
    }
}
