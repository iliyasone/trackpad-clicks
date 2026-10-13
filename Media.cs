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
        // Several tabs: the one playing, else the most recent.
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

    static GlobalSystemMediaTransportControlsSessionManager Manager() =>
        _manager ??= GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().Result;
}
