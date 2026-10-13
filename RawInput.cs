using System.Runtime.InteropServices;
using static TrackpadClicks.Native;

namespace TrackpadClicks;

// Raw HID reports from every precision touchpad, on a thread of their own: reading a
// device's description goes into the HID stack and must never hold up the mouse hook.
sealed class RawInput : NativeWindow
{
    // When the thread started on the current message, 0 when idle; watched by the tray.
    public static long BusySince;
    public static volatile string At = "idle";
    // Keep each report's bytes on its pad, for the settings window's test.
    public static volatile bool Capture;

    readonly Gestures _gestures;
    readonly Dictionary<IntPtr, Pad?> _pads = new();
    readonly ushort[] _usages = new ushort[32];
    readonly bool _debug = File.Exists(@"C:\TrackpadClicks\debug");
    bool _loggedParseError;

    public static void Start(Gestures gestures)
    {
        var thread = new Thread(() =>
        {
            var window = new RawInput(gestures);
            Application.Run();
            GC.KeepAlive(window);
        }) { IsBackground = true, Name = "raw input" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    RawInput(Gestures gestures)
    {
        _gestures = gestures;
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) /* HWND_MESSAGE */ });
        var dev = new[] { new RAWINPUTDEVICE { UsagePage = 0x0D, Usage = 0x05, Flags = RIDEV_INPUTSINK, Target = Handle } };
        if (!RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            Log.Write($"RegisterRawInputDevices failed {Marshal.GetLastWin32Error()}");
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_INPUT)
        {
            Interlocked.Exchange(ref BusySince, Environment.TickCount64);
            try { OnRawInput(m.LParam); }
            catch (Exception e) { Log.Write($"raw input: {e}"); }
            At = "idle";
            Interlocked.Exchange(ref BusySince, 0);
        }
        base.WndProc(ref m);
    }

    void OnRawInput(IntPtr handle)
    {
        At = "GetRawInputData";
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
            {
                At = "parse report";
                if (!Parse(pad, report, reportSize, out var frame)) continue;
                if (_debug && frame.Button != pad.Button)
                    Log.Write($"dev {header.Device} {(frame.Button ? "down" : "up")} {frame.Contacts} fingers, landed {pad.LandingsText} ms before the last");
                byte[]? raw = null;
                if (Capture)
                {
                    raw = new byte[reportSize];
                    Marshal.Copy(report, raw, 0, (int)reportSize);
                }
                At = "gestures";
                lock (_gestures.Sync)
                {
                    pad.Apply(frame);
                    pad.Raw = raw;
                    _gestures.OnReport(pad, frame.Button);
                }
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // Each finger is its own link collection holding a tip switch, contact ID and X/Y.
    // The Magic Trackpad driver fills only the first ContactCount of them; the rest hold
    // leftover memory, sometimes with the tip switch set, so only those slots are read.
    bool Parse(Pad pad, IntPtr report, uint length, out Frame frame)
    {
        frame = new Frame { Ids = new uint[Pad.MaxFingers], Xs = new double[Pad.MaxFingers], Ys = new double[Pad.MaxFingers], Pressures = new int[Pad.MaxFingers] };
        int status = HidP_GetUsageValue(HidP_Input, 0x0D, 0, 0x54, out uint contacts, pad.Preparsed, report, length);
        if (status != HIDP_STATUS_SUCCESS)
        {
            if (!_loggedParseError) Log.Write($"contact count parse failed 0x{status:X}");
            _loggedParseError = true;
            return false;
        }
        uint n = (uint)_usages.Length;
        frame.Button = HidP_GetUsages(HidP_Input, 0x09, 0, _usages, ref n, pad.Preparsed, report, length) == HIDP_STATUS_SUCCESS
                       && _usages.Take((int)n).Contains((ushort)1);
        frame.Contacts = (int)contacts;

        double sumX = 0, sumY = 0;
        int fingers = 0;
        int slots = Math.Min(frame.Contacts, pad.LinkCollections - 1);
        for (ushort link = 1; link <= slots; link++)
        {
            if (HidP_GetUsageValue(HidP_Input, 0x01, link, 0x31, out uint y, pad.Preparsed, report, length) != HIDP_STATUS_SUCCESS
                || y < pad.YMin || y > pad.YMax
                || HidP_GetUsageValue(HidP_Input, 0x01, link, 0x30, out uint x, pad.Preparsed, report, length) != HIDP_STATUS_SUCCESS
                || x < pad.XMin || x > pad.XMax)
                continue;
            n = (uint)_usages.Length;
            if (HidP_GetUsages(HidP_Input, 0x0D, link, _usages, ref n, pad.Preparsed, report, length) != HIDP_STATUS_SUCCESS
                || !_usages.Take((int)n).Contains((ushort)0x42))
                continue;
            if (fingers < Pad.MaxFingers)
            {
                HidP_GetUsageValue(HidP_Input, 0x0D, link, 0x51, out uint id, pad.Preparsed, report, length);
                frame.Ids[fingers] = id;
                frame.Xs[fingers] = x;
                frame.Ys[fingers] = y;
                // Per-finger pressure, 0..255: Magic Trackpad driver 2.2 and later; -1 without it.
                frame.Pressures[fingers] = HidP_GetUsageValue(HidP_Input, 0x0D, link, 0x30, out uint pressure, pad.Preparsed, report, length) == HIDP_STATUS_SUCCESS
                    ? (int)pressure : -1;
            }
            sumX += x;
            sumY += y;
            fingers++;
        }
        frame.X = fingers > 0 ? sumX / fingers : 0;
        frame.Y = fingers > 0 ? sumY / fingers : 0;
        frame.Fingers = Math.Min(fingers, Pad.MaxFingers);
        return true;
    }

    Pad? GetPad(IntPtr device)
    {
        if (_pads.TryGetValue(device, out var pad)) return pad;
        At = "GetRawInputDeviceInfo (new device)";
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
        if (pad != null)
            lock (_gestures.Sync) _gestures.Pads.Add(pad);
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
