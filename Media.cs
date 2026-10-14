using Windows.Media.Control;

namespace TrackpadClicks;

// Media keys go to whichever app Windows thinks is current: the agent's voice in T3 Code
// as often as the music. With a media app set (Settings.MediaApp, e.g. "chrome"),
// play/pause and the track keys go to that app's media session only, through Windows'
// media session list. Runs on the effects worker.
static class Media
{
    static GlobalSystemMediaTransportControlsSessionManager? _manager;

    // The sessions playing or paused now, by app id; for the settings window.
    public static string[] Apps()
    {
        try
        {
            return Manager().GetSessions().Select(s => s.SourceAppUserModelId).Distinct().ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    // False when no session of the app exists.
    public static bool Send(string app, GestureAction action)
    {
        var sessions = Manager().GetSessions().Where(s => s.SourceAppUserModelId.Contains(app, StringComparison.OrdinalIgnoreCase)).ToList();
        if (sessions.Count == 0) return false;
        // Several tabs: the one playing, else the first Windows lists.
        var session = sessions.FirstOrDefault(s => s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                      ?? sessions[0];
        var call = action switch
        {
            GestureAction.NextTrack => session.TrySkipNextAsync(),
            GestureAction.PrevTrack => session.TrySkipPreviousAsync(),
            _ => session.TryTogglePlayPauseAsync(),
        };
        call.AsTask().Wait(1000);
        return true;
    }

    // Every app but `except` (empty: every app): pauses those playing, else resumes the
    // one that played last. False when there is none.
    public static bool ToggleOther(string except)
    {
        var sessions = Manager().GetSessions()
            .Where(s => except.Length == 0 || !s.SourceAppUserModelId.Contains(except, StringComparison.OrdinalIgnoreCase)).ToList();
        if (sessions.Count == 0) return false;
        var playing = sessions.Where(s => s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing).ToList();
        if (playing.Count > 0)
        {
            // All at once, within one second together.
            Task.WaitAll(playing.Select(s => s.TryPauseAsync().AsTask()).ToArray(), 1000);
            return true;
        }
        var last = sessions.FirstOrDefault(s => s.SourceAppUserModelId == _lastPlayed) ?? sessions[0];
        last.TryPlayAsync().AsTask().Wait(1000);
        return true;
    }

    // The app other than the music app whose session last started playing, by app id;
    // kept up by Windows' events.
    static volatile string? _lastPlayed;
    static readonly HashSet<GlobalSystemMediaTransportControlsSession> Watched = new();

    // From the worker and the settings window: one manager, made once. A media service
    // that doesn't answer in 2 s throws, and the next call asks again.
    static GlobalSystemMediaTransportControlsSessionManager Manager()
    {
        lock (Watched)
        {
            if (_manager != null) return _manager;
            var request = GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask();
            if (!request.Wait(2000)) throw new TimeoutException("the media session service didn't answer");
            _manager = request.Result;
            _manager.SessionsChanged += (m, _) => Watch(m);
            Watch(_manager);
            return _manager;
        }
    }

    static void Watch(GlobalSystemMediaTransportControlsSessionManager manager)
    {
        lock (Watched)
            foreach (var session in manager.GetSessions())
            {
                if (!Watched.Add(session)) continue;
                session.PlaybackInfoChanged += (s, _) =>
                {
                    try
                    {
                        // The music app is left out: "what played last" is for the others.
                        string app = Settings.Current.MediaApp;
                        if (s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                            && (app.Length == 0 || !s.SourceAppUserModelId.Contains(app, StringComparison.OrdinalIgnoreCase)))
                            _lastPlayed = s.SourceAppUserModelId;
                    }
                    catch { }
                };
            }
    }

    // Started with the app, so the last one playing is known before the first gesture.
    public static void Start() => Worker.Post("media sessions", () => Manager());
}
