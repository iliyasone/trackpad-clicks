using System.Diagnostics;
using System.Text.Json;

namespace TrackpadClicks;

// Whether Ciao is dictating hands-free, so that one click on a dictation zone can stop
// what a double click started. Ciao writes %APPDATA%\ciao\state.json on every change:
// {"recording": true, "handsFree": true, "pid": 1234}. Read on the file watcher's thread
// pool thread; the hook only reads the flag. Without the file (an older Ciao) the flag
// stays false and a single click is a click.
static class Ciao
{
    public static volatile bool HandsFree;

    static readonly object ReadLock = new();
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ciao");
    static FileSystemWatcher? _watcher;

    public static void Start()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            _watcher = new FileSystemWatcher(Dir, "state.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            _watcher.Changed += (_, _) => Read();
            _watcher.Created += (_, _) => Read();
            _watcher.Renamed += (_, _) => Read();
            _watcher.Deleted += (_, _) => HandsFree = false;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception e) { Log.Write($"ciao state: no watch ({e.Message})"); }
        ThreadPool.QueueUserWorkItem(_ => Read());
    }

    // Every 5 s from the tray: a Ciao that crashed hands-free never says it stopped.
    public static void Recheck()
    {
        if (HandsFree) ThreadPool.QueueUserWorkItem(_ => Read());
    }

    // The watcher's events, the start-up read and the recheck may overlap.
    static void Read()
    {
        lock (ReadLock) ReadLocked();
    }

    static void ReadLocked()
    {
        // Unreadable twice (Ciao renaming it in): keep what was known; the next write or
        // recheck reads it again.
        bool on = HandsFree;
        for (int i = 0; i < 2; i++)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "state.json")));
                var root = doc.RootElement;
                on = root.TryGetProperty("recording", out var r) && r.GetBoolean()
                     && root.TryGetProperty("handsFree", out var h) && h.GetBoolean()
                     && (!root.TryGetProperty("pid", out var pid) || Alive(pid.GetInt32()));
                break;
            }
            catch (FileNotFoundException) { on = false; break; }
            catch { Thread.Sleep(30); }
        }
        if (on != HandsFree) Log.Write($"ciao: hands-free {(on ? "on" : "off")}");
        HandsFree = on;
    }

    // A Ciao that crashed leaves its last state behind; see Recheck.
    static bool Alive(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; }
        catch { return false; }
    }
}
