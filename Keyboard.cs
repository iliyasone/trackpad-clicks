using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using static TrackpadClicks.Native;

namespace TrackpadClicks;

// An Apple keyboard's top row does what is drawn on its keys, as on a Mac:
//   F1/F2 screen brightness down/up, F3 Lanes' overview (Task View without Lanes),
//   F7/F8/F9 previous/play-pause/next, F10/F11/F12 mute/volume down/volume up.
// Windows never sees the keyboard's fn key (it comes inside the keyboard's own report, which
// only Windows' keyboard driver reads), so the keys always do what is drawn on them. F4, F5
// and F6 stay F-keys, and so does any F-key pressed with Ctrl, Alt, Shift or Win. Also as on
// a Mac (Cmd is the Windows key): Ctrl+Cmd+F full screen (F11), Cmd+Shift+3 a picture of the
// screen (Win+Print Screen), Cmd+Shift+4 or 5 a picture of a part of it (Win+Shift+S).
//
// The hook runs for every key in the system: it decides from memory and the key state only;
// what the keys do goes through Worker. With gestures off, keys pass as they are.
static class Keyboard
{
    const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;
    const uint LLKHF_INJECTED = 0x10;
    const int VK_SHIFT = 0x10, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_TAB = 0x09, VK_SNAPSHOT = 0x2C, VK_F11 = 0x7A;

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT { public uint VkCode, ScanCode, Flags, Time; public IntPtr ExtraInfo; }

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int RegisterWindowMessage(string name);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc proc, IntPtr param);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);

    static HookProc? _proc;
    static IntPtr _hook;
    // Keys held now whose press the hook took: their repeats and release are taken too.
    static readonly HashSet<uint> Taken = new();
    static volatile bool _brightnessBusy;

    public static void Start()
    {
        var thread = new Thread(() =>
        {
            _proc = Proc;
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero) Log.Write($"keyboard hook failed {Marshal.GetLastWin32Error()}");
            Application.Run();
        }) { IsBackground = true, Name = "keyboard hook" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    static IntPtr Proc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && Gestures.Enabled)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if ((k.Flags & LLKHF_INJECTED) == 0 && Take(k.VkCode, (int)wParam is WM_KEYDOWN or WM_SYSKEYDOWN)) return 1;
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    static bool Held(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    // True to swallow the key.
    static bool Take(uint vk, bool down)
    {
        if (!down) return Taken.Remove(vk);
        bool repeat = Taken.Contains(vk);
        bool ctrl = Held(VK_CONTROL), alt = Held(VK_MENU), shift = Held(VK_SHIFT), win = Held(VK_LWIN) || Held(VK_RWIN);
        string? what = null;
        if (!ctrl && !alt && !shift && !win)
        {
            switch (vk)
            {
                case 0x70: what = "brightness down"; Brightness(-10); break;
                case 0x71: what = "brightness up"; Brightness(+10); break;
                case 0x72: what = "overview"; if (!repeat) Overview(); break;
                case 0x76: what = "previous"; if (!repeat) Effects.Key(VK_MEDIA_PREV_TRACK); break;
                case 0x77: what = "play/pause"; if (!repeat) Effects.Key(VK_MEDIA_PLAY_PAUSE); break;
                case 0x78: what = "next"; if (!repeat) Effects.Key(VK_MEDIA_NEXT_TRACK); break;
                case 0x79: what = "mute"; if (!repeat) Effects.Key(VK_VOLUME_MUTE); break;
                case 0x7A: what = "volume down"; Effects.Key(VK_VOLUME_DOWN); break;
                case 0x7B: what = "volume up"; Effects.Key(VK_VOLUME_UP); break;
            }
        }
        else if (ctrl && win && !alt && !shift && vk == 'F')
        {
            what = "full screen";
            if (!repeat) Chord(release: new[] { VK_CONTROL, VK_LWIN, VK_RWIN }, VK_F11);
        }
        else if (win && shift && !ctrl && !alt && vk == '3')
        {
            what = "screen picture";
            if (!repeat) Chord(release: new[] { VK_SHIFT }, VK_SNAPSHOT);
        }
        else if (win && shift && !ctrl && !alt && vk is '4' or '5')
        {
            what = "screen part picture";
            if (!repeat) Chord(release: Array.Empty<int>(), 'S');
        }
        if (what == null) return false;
        Taken.Add(vk);
        if (!repeat) Log.Write($"key 0x{vk:X2}: {what}");
        return true;
    }

    // Lets go of the given modifiers (an unassigned key first, so letting go of Win does not open
    // Start), taps `key` with the ones still held, and holds the let-go ones again if the
    // fingers still do. The press that asked for it is already taken.
    static void Chord(int[] release, int key) => Worker.Post($"keys for 0x{key:X2}", () =>
    {
        var up = release.Where(Held).ToList();
        if (up.Count > 0) Send(VK_NONE, false, true);
        foreach (var vk in up) Send(vk, true);
        Send(key, false);
        Send(key, true);
        foreach (var vk in up) if (Held(vk)) Send(vk, false);
    });

    static void Send(int vk, bool up, bool tap = false)
    {
        uint ext = vk is VK_LWIN or VK_RWIN or VK_SNAPSHOT ? KEYEVENTF_EXTENDEDKEY : 0;
        var input = new INPUT { Type = INPUT_KEYBOARD, U = new InputUnion { Keyboard = new KEYBDINPUT { Vk = (ushort)vk, Flags = ext | (up ? KEYEVENTF_KEYUP : 0) } } };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        if (tap) Send(vk, true);
    }

    // Lanes (github.com/iliyasone/lanes) opens or closes its overview on the message
    // "Lanes.Overview" posted to its window titled "Lanes"; without Lanes, Task View opens.
    static void Overview() => Worker.Post("overview", () =>
    {
        var lanes = Process.GetProcessesByName("Lanes").Select(p => (uint)p.Id).ToHashSet();
        IntPtr target = IntPtr.Zero;
        if (lanes.Count > 0)
            EnumWindows((h, _) =>
            {
                GetWindowThreadProcessId(h, out uint pid);
                if (!lanes.Contains(pid)) return true;
                var title = new StringBuilder(16);
                GetWindowText(h, title, title.Capacity);
                if (title.ToString() != "Lanes") return true;
                target = h;
                return false;
            }, IntPtr.Zero);
        if (target != IntPtr.Zero && PostMessage(target, RegisterWindowMessage("Lanes.Overview"), IntPtr.Zero, IntPtr.Zero)) return;
        Send(VK_LWIN, false);
        Send(VK_TAB, false);
        Send(VK_TAB, true);
        Send(VK_LWIN, true);
    });

    // The laptop's own screen, through Windows' brightness setting (an external monitor has
    // none). Held, it goes on in steps; a step still being made lets the next repeat pass.
    static void Brightness(int step)
    {
        if (_brightnessBusy) return;
        _brightnessBusy = true;
        Worker.Post($"brightness {step:+0;-0}", () =>
        {
            try
            {
                using var now = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
                int level = now.Get().Cast<ManagementObject>().Select(o => (int)(byte)o["CurrentBrightness"]).DefaultIfEmpty(-1).First();
                if (level < 0)
                {
                    Log.WriteNow("brightness: no screen whose brightness Windows sets");
                    return;
                }
                byte to = (byte)Math.Clamp(level + step, 0, 100);
                using var set = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
                foreach (ManagementObject o in set.Get())
                    using (o) o.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, to });
            }
            finally { _brightnessBusy = false; }
        });
    }
}
