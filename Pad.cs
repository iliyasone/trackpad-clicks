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

    public void Apply(in Frame frame)
    {
        Contacts = frame.Contacts;
        LastReport = Environment.TickCount64;
        X = frame.X;
        Y = frame.Y;
        YFingers = frame.Fingers;
        Array.Copy(frame.Ids, Ids, MaxFingers);
        Array.Copy(frame.Xs, Xs, MaxFingers);
        Array.Copy(frame.Ys, Ys, MaxFingers);
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
