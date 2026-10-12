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
    // Where each finger was when it last moved, and when that was (0: not since landing).
    readonly Dictionary<uint, (double X, double Y, long At)> _moved = new();
    readonly List<uint> _lifted = new();

    // Travel, as a share of the pad's width, that counts as the finger moving (~3 mm).
    const double MoveStep = 0.02;

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

        // Some reports carry the contact count without readable fingers: forget only
        // fingers missing from a report that has them all.
        var ids = frame.Ids.AsSpan(0, frame.Fingers);
        if (frame.Fingers >= frame.Contacts)
        {
            _lifted.Clear();
            foreach (var id in Landed.Keys)
                if (!ids.Contains(id)) _lifted.Add(id);
            foreach (var id in _lifted)
            {
                Landed.Remove(id);
                _moved.Remove(id);
            }
        }
        double step = MoveStep * Math.Max(1, XMax - XMin);
        for (int i = 0; i < frame.Fingers; i++)
        {
            uint id = frame.Ids[i];
            Landed.TryAdd(id, now);
            if (!_moved.TryGetValue(id, out var m)) _moved[id] = (frame.Xs[i], frame.Ys[i], 0);
            else if (Math.Abs(frame.Xs[i] - m.X) + Math.Abs(frame.Ys[i] - m.Y) > step) _moved[id] = (frame.Xs[i], frame.Ys[i], now);
        }
    }

    // How many fingers that landed before `before` have moved since `since`: a finger
    // pointing, as opposed to one resting on the pad (a thumb).
    public int MovedSince(long since, long before) =>
        Landed.Count(f => f.Value < before && _moved.TryGetValue(f.Key, out var m) && m.At >= since);

    // When the last finger landed; a contact the reports have not shown yet, just now.
    public long LastLanding => Landed.Count < Contacts || Landed.Count == 0 ? LastReport : Landed.Values.Max();

    // How long before the last finger to land each finger landed, longest first.
    // A contact the reports have not shown yet has just landed.
    public long[] Landings()
    {
        var at = Landed.Values.OrderDescending().Take(Contacts).ToList();
        while (at.Count < Contacts) at.Insert(0, LastReport);
        long last = at.Count > 0 ? at.Max() : 0;
        return at.Select(t => last - t).OrderDescending().ToArray();
    }

    public string LandingsText => string.Join("/", Landings());
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
