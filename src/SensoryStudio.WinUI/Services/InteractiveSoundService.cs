using SensoryStudio.WinUI.Experiences;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace SensoryStudio.WinUI.Services;

public sealed class InteractiveSoundService : IDisposable, IAudioExperience
{
    private const int PoolSize = 4;
    private readonly Dictionary<string, MediaPlayer[]> _players = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _nextPlayer = new(StringComparer.OrdinalIgnoreCase);

    public void Initialize()
    {
        AddPool("stone", "Rocks.wav");
        AddPool("sand", "Sand raking.wav");
        SoundSettingsService.Register(this);
    }

    public void PlayStone() => Play("stone");

    public void PlaySand() => Play("sand");

    public void SetVolume(double volume)
    {
        foreach (MediaPlayer player in _players.Values.SelectMany(pool => pool))
        {
            player.Volume = Math.Clamp(volume, 0, 1);
            if (volume <= 0)
                player.Pause();
        }
    }

    public void Dispose()
    {
        SoundSettingsService.Unregister(this);
        foreach (MediaPlayer player in _players.Values.SelectMany(pool => pool))
            player.Dispose();
        _players.Clear();
        _nextPlayer.Clear();
    }

    private void AddPool(string key, string fileName)
    {
        string path = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Interactive Sounds",
            fileName);
        if (!File.Exists(path))
            return;

        var players = new MediaPlayer[PoolSize];
        for (int index = 0; index < players.Length; index++)
        {
            players[index] = new MediaPlayer
            {
                AutoPlay = false,
                Volume = AppSettingsService.Current.EffectiveSoundVolume,
                Source = MediaSource.CreateFromUri(new Uri(path)),
            };
        }
        _players[key] = players;
        _nextPlayer[key] = 0;
    }

    private void Play(string key)
    {
        if (AppSettingsService.Current.EffectiveSoundVolume <= 0)
            return;

        if (!_players.TryGetValue(key, out MediaPlayer[]? players))
            return;

        int index = _nextPlayer[key];
        _nextPlayer[key] = (index + 1) % players.Length;
        MediaPlayer player = players[index];
        player.Volume = AppSettingsService.Current.EffectiveSoundVolume;
        player.PlaybackSession.Position = TimeSpan.Zero;
        player.Play();
    }
}
