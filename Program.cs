// TrackpadClicks: Mac-like physical-click gestures for a Magic Trackpad on Windows
// (what each click does: Gestures.cs). Taps are off in Windows settings.
//
// Four threads, so that nothing can make the mouse lag:
//   mouse hook  — decides swallow/pass from memory only (MouseHook.cs);
//   raw input   — reads the touchpads' HID reports (RawInput.cs);
//   effects     — SendInput, volume, window moves, the log (Effects.cs);
//   UI          — the tray icon, and the watch over the other two.
// Every mouse event in the system waits for the hook, so a call that hangs must never
// run on it. A stuck raw input or effects thread only switches gestures off, shown in
// the tray.

using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace TrackpadClicks;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // One instance. Not a mutex: a copy wedged in the kernel keeps its mutex but
        // reports HasExited, and must not block its replacement.
        int replacing = args.Length == 2 && args[0] == "--replace" ? int.Parse(args[1]) : 0;
        int me = Environment.ProcessId;
        foreach (var other in Process.GetProcessesByName("TrackpadClicks"))
        {
            try
            {
                if (other.Id != me && other.Id != replacing && !other.HasExited) return;
            }
            catch { }
        }

        // Hook coordinates and window rects must both be physical pixels.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Log.WriteNow("start");
        Worker.Start();
        var gestures = new Gestures();
        RawInput.Start(gestures);
        MouseHook.Start(gestures);
        Application.Run(new Tray(gestures));
    }
}

sealed class Tray : ApplicationContext
{
    const int StuckMs = 2000;

    readonly Gestures _gestures;
    readonly NotifyIcon _icon;
    readonly ToolStripMenuItem _enabled;
    readonly Icon _on = Draw(Color.White), _off = Draw(Color.Gray), _stuckIcon = Draw(Color.OrangeRed);
    string? _stuck;

    public Tray(Gestures gestures)
    {
        _gestures = gestures;
        _enabled = new ToolStripMenuItem("Жесты включены") { Checked = true, CheckOnClick = true };
        _enabled.CheckedChanged += (_, _) => SetEnabled(_enabled.Checked);
        var menu = new ContextMenuStrip();
        menu.Items.Add(_enabled);
        menu.Items.Add("Открыть лог", null, (_, _) => Process.Start(new ProcessStartInfo(Log.Path) { UseShellExecute = true }));
        menu.Items.Add("Перезапустить", null, (_, _) => Restart());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выйти", null, (_, _) => Quit());
        _icon = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        Show();

        var timer = new System.Windows.Forms.Timer { Interval = 500 };
        timer.Tick += (_, _) => Watch();
        timer.Start();
    }

    void SetEnabled(bool on)
    {
        Gestures.Enabled = on;
        if (!on) lock (_gestures.Sync) _gestures.Reset();
        Log.Write(on ? "gestures on" : "gestures off");
        Show();
    }

    // A thread busy on one item for too long is stuck: gestures off, say so.
    void Watch()
    {
        if (_stuck != null) return;
        _stuck = Stuck(ref Worker.BusySince, Worker.At, "действия")
                 ?? Stuck(ref RawInput.BusySince, RawInput.At, "чтение трекпада");
        if (_stuck == null) return;
        Gestures.Enabled = false;
        Log.WriteNow($"stuck: {_stuck}; gestures off");
        Show();
        _icon.ShowBalloonTip(10000, "TrackpadClicks завис",
            $"{_stuck}. Жесты выключены, мышь работает как обычно. Перезапустить можно в меню значка.", ToolTipIcon.Warning);
    }

    static string? Stuck(ref long since, string at, string thread)
    {
        long start = Interlocked.Read(ref since);
        if (start == 0) return null;
        long ms = Environment.TickCount64 - start;
        return ms > StuckMs ? $"Поток «{thread}» завис на «{at}» ({ms / 1000} с)" : null;
    }

    void Show()
    {
        _icon.Icon = _stuck != null ? _stuckIcon : Gestures.Enabled ? _on : _off;
        _icon.Text = _stuck != null ? "TrackpadClicks: завис, жесты выключены"
            : Gestures.Enabled ? "TrackpadClicks: жесты включены" : "TrackpadClicks: жесты выключены";
    }

    void Restart()
    {
        Log.WriteNow("restart from tray");
        Process.Start(Environment.ProcessPath!, $"--replace {Environment.ProcessId}");
        Quit();
    }

    void Quit()
    {
        _icon.Visible = false;
        Environment.Exit(0);
    }

    // A small trackpad: rounded rectangle, filled with the state's color.
    static Icon Draw(Color fill)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = new GraphicsPath();
            var r = new Rectangle(2, 6, 28, 20);
            const int d = 10;
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);
            using var pen = new Pen(Color.FromArgb(50, 50, 50), 2.5f);
            g.DrawPath(pen, path);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }
}

static class Log
{
    public static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrackpadClicks", "log.txt");
    static readonly object FileLock = new();

    // From any thread, the hook included: the write itself happens on the effects worker.
    public static void Write(string line)
    {
        var at = DateTime.Now;
        Worker.Post("log write", () => WriteNow(line, at));
    }

    public static void WriteNow(string line, DateTime? at = null)
    {
        try
        {
            lock (FileLock)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                if (File.Exists(Path) && new FileInfo(Path).Length > 1_000_000) File.Delete(Path);
                File.AppendAllText(Path, $"{at ?? DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}\n");
            }
        }
        catch { }
    }
}
