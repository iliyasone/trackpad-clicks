// Physical click with 3 fingers = middle button, with 4+ fingers = media play/pause.
// Taps are left to Windows settings (turned off there); this only remaps real clicks.
//
// Finger count and button state come from the precision touchpad's raw HID reports.
// The left/right click Windows synthesizes for the same press is swallowed by a
// low-level mouse hook. Both run on the one UI thread, so no locking is needed.

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
        Log.Write("start");
        var watcher = new Watcher();
        Application.Run();
        GC.KeepAlive(watcher);
    }
}

enum Action { None, Middle, PlayPause }

sealed class Watcher : NativeWindow
{
    // Our own SendInput calls carry this tag, though the hook already skips injected input.
    static readonly IntPtr Tag = new(0x7C11C);
    const long FreshMs = 250;

    readonly HookProc _hookProc;
    readonly IntPtr _hook;
    readonly Dictionary<IntPtr, IntPtr> _preparsed = new();
    readonly ushort[] _usages = new ushort[32];

    // The built-in ASUS pad streams reports too (here: constantly "pressed", 0 fingers),
    // so every touchpad keeps its own state.
    sealed class Pad { public int Contacts; public long LastReport; public bool Button; }
    readonly Dictionary<IntPtr, Pad> _pads = new();
    readonly bool _debug = File.Exists(@"C:\TrackpadClicks\debug");

    Action _active;
    Pad? _activePad;
    bool _swallowing;
    bool _loggedParseError;

    public Watcher()
    {
        CreateHandle(new CreateParams());
        var dev = new[] { new RAWINPUTDEVICE { UsagePage = 0x0D, Usage = 0x05, Flags = RIDEV_INPUTSINK, Target = Handle } };
        if (!RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            Log.Write($"RegisterRawInputDevices failed {Marshal.GetLastWin32Error()}");
        _hookProc = Hook;
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _hookProc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) Log.Write($"SetWindowsHookEx failed {Marshal.GetLastWin32Error()}");
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
            IntPtr preparsed = Preparsed(header.Device);
            if (preparsed == IntPtr.Zero) return;

            uint reportSize = (uint)Marshal.ReadInt32(buf, (int)headerSize);
            uint count = (uint)Marshal.ReadInt32(buf, (int)headerSize + 4);
            IntPtr report = buf + (int)headerSize + 8;
            for (uint i = 0; i < count; i++, report += (int)reportSize)
                OnReport(header.Device, preparsed, report, reportSize);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    void OnReport(IntPtr device, IntPtr preparsed, IntPtr report, uint length)
    {
        int status = HidP_GetUsageValue(HidP_Input, 0x0D, 0, 0x54, out uint contacts, preparsed, report, length);
        if (status != HIDP_STATUS_SUCCESS)
        {
            if (!_loggedParseError) Log.Write($"contact count parse failed 0x{status:X}");
            _loggedParseError = true;
            return;
        }
        uint n = (uint)_usages.Length;
        bool button = HidP_GetUsages(HidP_Input, 0x09, 0, _usages, ref n, preparsed, report, length) == HIDP_STATUS_SUCCESS
                      && _usages.Take((int)n).Contains((ushort)1);

        if (!_pads.TryGetValue(device, out var pad)) _pads[device] = pad = new Pad();
        pad.Contacts = (int)contacts;
        pad.LastReport = Environment.TickCount64;

        if (button != pad.Button && _debug)
        {
            var bytes = new byte[length];
            Marshal.Copy(report, bytes, 0, (int)length);
            Log.Write($"dev {device} {(button ? "down" : "up")} {contacts} fingers {Convert.ToHexString(bytes)}");
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
    }

    // A pad with 3+ fingers on it right now, if any.
    Pad? MultiFingerPad()
    {
        long now = Environment.TickCount64;
        foreach (var pad in _pads.Values)
            if (pad.Contacts >= 3 && now - pad.LastReport < FreshMs) return pad;
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
            _active = Action.PlayPause;
            Key(VK_MEDIA_PLAY_PAUSE);
        }
        else
        {
            _active = Action.Middle;
            Mouse(MOUSEEVENTF_MIDDLEDOWN);
        }
        Log.Write($"{_active} ({fingers} fingers)");
    }

    void End()
    {
        if (_active == Action.Middle) Mouse(MOUSEEVENTF_MIDDLEUP);
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

    IntPtr Preparsed(IntPtr device)
    {
        if (_preparsed.TryGetValue(device, out var p)) return p;
        uint size = 0;
        GetRawInputDeviceInfo(device, RIDI_PREPARSEDDATA, IntPtr.Zero, ref size);
        p = IntPtr.Zero;
        if (size > 0)
        {
            p = Marshal.AllocHGlobal((int)size);
            if ((int)GetRawInputDeviceInfo(device, RIDI_PREPARSEDDATA, p, ref size) <= 0)
            {
                Marshal.FreeHGlobal(p);
                p = IntPtr.Zero;
            }
        }
        _preparsed[device] = p;
        Log.Write($"touchpad {DeviceName(device)}");
        return p;
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
