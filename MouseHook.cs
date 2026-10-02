using System.Runtime.InteropServices;
using static TrackpadClicks.Native;

namespace TrackpadClicks;

// The low-level mouse hook, alone on its own thread. Every mouse event in the system
// waits for this callback, so it only decides "swallow or pass" from state in memory:
// no I/O, no SendInput, no calls that wait on other windows. Everything else is posted
// to the Effects worker.
static class MouseHook
{
    static HookProc? _proc;
    static IntPtr _hook;

    public static void Start(Gestures gestures)
    {
        var thread = new Thread(() =>
        {
            _proc = (code, wParam, lParam) => Proc(gestures, code, wParam, lParam);
            _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero) Log.Write($"SetWindowsHookEx failed {Marshal.GetLastWin32Error()}");
            Application.Run();
        }) { IsBackground = true, Name = "mouse hook" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    static IntPtr Proc(Gestures gestures, int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && Gestures.Enabled)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if ((info.Flags & LLMHF_INJECTED) == 0)
            {
                bool swallow;
                lock (gestures.Sync) swallow = gestures.OnMouse((int)wParam, info);
                if (swallow) return 1;
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }
}
