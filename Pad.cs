namespace TrackpadClicks;

// One touchpad. The built-in ASUS pad streams reports too (here: constantly "pressed",
// 0 fingers), so every touchpad keeps its own state. Written by the raw input thread
// and read by the mouse hook, both under Gestures.Sync.
sealed class Pad
{
    public const int MaxFingers = 8;

    public IntPtr Preparsed;
    public ushort LinkCollections;
    public int XMin, XMax, YMin, YMax;

    public int Contacts;
    public long LastReport;
    public bool Button;
    // Average position of the fingers touching, and how many fingers it covers.
    public double X, Y;
    public int YFingers;
    // The same fingers one by one: contact ID and position.
    public readonly uint[] Ids = new uint[MaxFingers];
    public readonly double[] Xs = new double[MaxFingers], Ys = new double[MaxFingers];
    // When each finger now touching landed, by contact ID.
    public readonly Dictionary<uint, long> Landed = new();
    readonly List<uint> _lifted = new();

    public void Apply(in Frame frame)
    {
        long now = Environment.TickCount64;
        Contacts = frame.Contacts;
        LastReport = now;
        X = frame.X;
        Y = frame.Y;
        YFingers = frame.Fingers;
        Array.Copy(frame.Ids, Ids, MaxFingers);
        Array.Copy(frame.Xs, Xs, MaxFingers);
        Array.Copy(frame.Ys, Ys, MaxFingers);

        var ids = frame.Ids.AsSpan(0, frame.Fingers);
        _lifted.Clear();
        foreach (var id in Landed.Keys)
            if (!ids.Contains(id)) _lifted.Add(id);
        foreach (var id in _lifted) Landed.Remove(id);
        foreach (var id in ids) Landed.TryAdd(id, now);
    }

    // Fingers that landed together with the last one to land, within windowMs of it.
    // Fingers resting on the pad before that are not counted. Without finger
    // positions in the reports, every contact counts.
    public int Together(long windowMs)
    {
        if (YFingers == 0) return Contacts;
        long last = 0;
        for (int i = 0; i < YFingers; i++) last = Math.Max(last, Landed.GetValueOrDefault(Ids[i]));
        int together = 0;
        for (int i = 0; i < YFingers; i++)
            if (last - Landed.GetValueOrDefault(Ids[i]) <= windowMs) together++;
        return together;
    }

    // How long before the last finger each finger landed, for the log.
    public string Landings()
    {
        long last = 0;
        for (int i = 0; i < YFingers; i++) last = Math.Max(last, Landed.GetValueOrDefault(Ids[i]));
        return string.Join("/", Enumerable.Range(0, YFingers).Select(i => last - Landed.GetValueOrDefault(Ids[i])).OrderDescending());
    }
}

// One parsed report, built outside the lock.
struct Frame
{
    public int Contacts;
    public bool Button;
    public double X, Y;
    public int Fingers;
    public uint[] Ids;
    public double[] Xs, Ys;
}
