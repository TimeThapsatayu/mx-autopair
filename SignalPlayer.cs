// The signal sound: the built-in signal.mp3 (embedded in the exe, so it arrives with signed updates) played
// on a loop while the command center has it turned on. Volume is relative to the Windows volume.
using System.Reflection;
using Windows.Media.Core;
using Windows.Media.Playback;

sealed class SignalPlayer(Action<string> log, bool muted = false)
{
    readonly object gate = new();
    MediaPlayer? player;

    public void Apply(SignalSettings settings)
    {
        lock (gate)
        {
            try
            {
                if (!settings.Playing)
                {
                    if (player == null) return;
                    player.Pause();
                    player.Dispose();
                    player = null;
                    log("Signal sound: off");
                    return;
                }
                if (player != null)
                {
                    if (Math.Abs(player.Volume - settings.Volume) > 0.001) log($"Signal sound: volume {settings.Volume:0%}");
                    player.Volume = settings.Volume;
                    return;
                }
                var sound = new MemoryStream();
                using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("signal.mp3")!)
                    resource.CopyTo(sound);
                sound.Position = 0;
                player = new MediaPlayer
                {
                    IsLoopingEnabled = true,
                    Volume = settings.Volume,
                    IsMuted = muted,
                    Source = MediaSource.CreateFromStream(sound.AsRandomAccessStream(), "audio/mpeg"),
                };
                player.CommandManager.IsEnabled = false;  // no media overlay or media keys for it
                player.MediaFailed += (_, e) => log($"Signal sound: can't play ({e.Error}: {e.ErrorMessage})");
                player.Play();
                log($"Signal sound: on, looping at {settings.Volume:0%}");
            }
            catch (Exception ex) { log($"Signal sound error: {ex.Message}"); }
        }
    }

    // For --signal-test.
    public string Describe()
    {
        lock (gate)
            return player == null
                ? "no player"
                : $"{player.PlaybackSession.PlaybackState}, position {player.PlaybackSession.Position.TotalSeconds:0.0}s of {player.PlaybackSession.NaturalDuration.TotalSeconds:0.0}s, volume {player.Volume:0%}, muted {player.IsMuted}";
    }
}
