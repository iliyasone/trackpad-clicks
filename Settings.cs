using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrackpadClicks;

// What each gesture does, chosen in the settings window and kept in
// %APPDATA%\TrackpadClicks\config.json. A Config is never changed once published:
// the window sends a whole new one, and Gestures reads Settings.Current under its lock.

enum GestureAction { Windows, MiddleClick, PlayPause, NextTrack, PrevTrack, VolumeUp, VolumeDown, Mute }

// Click: the action goes on the press. Hold: a short click stays a normal click, the
// action goes once the pad has been held down for HoldMs.
enum Trigger { Click, Hold }

sealed record Binding(GestureAction Action, Trigger Trigger = Trigger.Click);

sealed record Config
{
    // Gesture slots: "left-top", "left-middle", "left-bottom", the same for "right-",
    // and "three", "four" for clicks with that many fingers.
    public static readonly string[] Slots =
        { "left-top", "left-middle", "left-bottom", "right-top", "right-middle", "right-bottom", "three", "four" };

    public Dictionary<string, Binding> Gestures { get; init; } = new()
    {
        // The wide left strip acts only on a hold, so a short click there stays a click.
        ["left-top"] = new(GestureAction.NextTrack, Trigger.Hold),
        ["left-middle"] = new(GestureAction.MiddleClick, Trigger.Hold),
        ["left-bottom"] = new(GestureAction.PrevTrack, Trigger.Hold),
        ["right-top"] = new(GestureAction.VolumeUp),
        ["right-middle"] = new(GestureAction.PlayPause),
        ["right-bottom"] = new(GestureAction.VolumeDown),
        ["three"] = new(GestureAction.MiddleClick),
        ["four"] = new(GestureAction.Windows),
    };

    // Width of the left and right edge strips, in percent of the pad's width.
    public int LeftEdge { get; init; } = 30;
    public int RightEdge { get; init; } = 20;
    public int HoldMs { get; init; } = 400;
    // While an edge zone holds the middle button, another finger pressing at least this
    // hard (0..255, driver 2.2+) is a second click: left, or right with two fingers.
    public bool SecondPress { get; init; } = true;
    public int PressForce { get; init; } = 70;

    public Binding For(string slot) => Gestures.TryGetValue(slot, out var b) ? b : new(GestureAction.Windows);

    // Fills slots the file lacks with defaults and keeps numbers in range.
    public Config Checked()
    {
        var gestures = new Config().Gestures;
        foreach (var slot in Slots)
            if (Gestures.TryGetValue(slot, out var b) && b != null) gestures[slot] = b;
        return this with
        {
            Gestures = gestures,
            LeftEdge = Math.Clamp(LeftEdge, 5, 45),
            RightEdge = Math.Clamp(RightEdge, 5, 45),
            HoldMs = Math.Clamp(HoldMs, 150, 2000),
            PressForce = Math.Clamp(PressForce, 10, 250),
        };
    }
}

static class Settings
{
    public static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TrackpadClicks", "config.json");

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        WriteIndented = true,
    };

    public static volatile Config Current = Load();

    static Config Load()
    {
        try
        {
            if (File.Exists(Path))
                return (JsonSerializer.Deserialize<Config>(File.ReadAllText(Path), Json) ?? new()).Checked();
        }
        catch (Exception e) { Log.WriteNow($"config.json unreadable, defaults used: {e.Message}"); }
        return new Config();
    }

    // From the UI thread: publish, then write the file through a temporary one so a
    // crash never leaves half a file.
    public static void Apply(Config config)
    {
        Current = config.Checked();
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path + ".tmp", JsonSerializer.Serialize(Current, Json));
            File.Move(Path + ".tmp", Path, true);
        }
        catch (Exception e) { Log.WriteNow($"config.json not saved: {e.Message}"); }
    }
}
