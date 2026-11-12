using Godot;
using Sim.Core.Events;
using Sim.Core.Settlements;

namespace MedievalClient.Audio;

/// <summary>
/// Plays the game's procedurally generated sounds: looping countryside ambience, hammering from
/// construction sites near the camera (positional), UI clicks, building placement/demolition, and
/// bells for notable events. M toggles sound.
/// </summary>
public partial class SoundManager : Node3D
{
    private const int HammerVoices = 4;
    private const float HammerHearingDistance = 45f;

    private readonly Dictionary<string, AudioStreamWav> _sounds = new();
    private readonly Dictionary<string, double> _lastPlayed = new();
    private readonly List<AudioStreamPlayer3D> _hammers = new();
    private readonly System.Random _random = new(1234); // cosmetic only, never touches the simulation
    private AudioStreamPlayer _ui = null!;
    private AudioStreamPlayer _ambient = null!;
    private AudioStreamPlayer _bells = null!;
    private Settlement _settlement = null!;
    private Func<Vector3> _listenerFocus = () => Vector3.Zero;
    private double _clock;
    private double _nextHammer;

    public bool Muted { get; private set; }

    public void Initialize(Settlement settlement, Func<Vector3> listenerFocus)
    {
        _settlement = settlement;
        _listenerFocus = listenerFocus;

        _sounds["click"] = SoundSynth.Click();
        _sounds["place"] = SoundSynth.Place();
        _sounds["demolish"] = SoundSynth.Demolish();
        _sounds["hammer"] = SoundSynth.Hammer();
        _sounds["chime"] = SoundSynth.Bell(880f, 1.6f, brightness: 0.7f);
        _sounds["bell"] = SoundSynth.Bell(196f, 3.0f);
        _sounds["ambience"] = SoundSynth.Ambience();

        _ui = new AudioStreamPlayer { VolumeDb = -8f, MaxPolyphony = 4 };
        _bells = new AudioStreamPlayer { VolumeDb = -12f, MaxPolyphony = 3 };
        _ambient = new AudioStreamPlayer { Stream = _sounds["ambience"], VolumeDb = -14f };
        AddChild(_ui);
        AddChild(_bells);
        AddChild(_ambient);

        for (int i = 0; i < HammerVoices; i++)
        {
            var player = new AudioStreamPlayer3D
            {
                Stream = _sounds["hammer"],
                UnitSize = 8f,
                MaxDistance = HammerHearingDistance,
                VolumeDb = -6f,
            };
            AddChild(player);
            _hammers.Add(player);
        }

        _ambient.Play();
    }

    public void ToggleMute()
    {
        Muted = !Muted;
        AudioServer.SetBusMute(AudioServer.GetBusIndex("Master"), Muted);
    }

    public void Click() => Play(_ui, "click", minGap: 0.03);
    public void Placed() => Play(_ui, "place", minGap: 0.05);
    public void Demolished() => Play(_ui, "demolish", minGap: 0.2);

    /// <summary>Bells for things worth noticing, rate-limited so busy days don't turn into noise.</summary>
    public void OnEvents(IEnumerable<GameEvent> events)
    {
        foreach (var e in events)
        {
            switch (e.Kind)
            {
                case EventKind.BuildingCompleted:
                    Play(_bells, "chime", minGap: 2.0, pitchJitter: 0.04f);
                    break;
                case EventKind.Death:
                case EventKind.Starvation:
                    Play(_bells, "bell", minGap: 4.0);
                    break;
            }
        }
    }

    /// <summary>Called every frame: hammering from active construction sites near where the camera looks.</summary>
    public void Tick(double delta, bool simulationRunning)
    {
        _clock += delta;
        if (!simulationRunning || _settlement == null || _clock < _nextHammer) return;
        _nextHammer = _clock + 0.18 + _random.NextDouble() * 0.45;

        var focus = _listenerFocus();
        var sites = _settlement.Buildings
            .Where(b => b.State == BuildingState.UnderConstruction && b.WorkerIds.Count > 0)
            .Select(b => (Building: b, Position: Coords.BuildingCenter(b)))
            .Where(x => x.Position.DistanceTo(focus) < HammerHearingDistance)
            .ToList();
        if (sites.Count == 0) return;

        var site = sites[_random.Next(sites.Count)];
        var voice = _hammers.FirstOrDefault(h => !h.Playing) ?? _hammers[_random.Next(_hammers.Count)];
        voice.Position = site.Position + new Vector3((float)_random.NextDouble() - 0.5f, 0.4f, (float)_random.NextDouble() - 0.5f);
        voice.PitchScale = 0.85f + (float)_random.NextDouble() * 0.3f;
        voice.Play();
    }

    private void Play(AudioStreamPlayer player, string sound, double minGap, float pitchJitter = 0.03f)
    {
        if (_lastPlayed.TryGetValue(sound, out double last) && _clock - last < minGap) return;
        _lastPlayed[sound] = _clock;
        player.Stream = _sounds[sound];
        player.PitchScale = 1f + ((float)_random.NextDouble() * 2f - 1f) * pitchJitter;
        player.Play();
    }

    /// <summary>Writes every sound to WAV files (for listening to them outside the game, or checking them).</summary>
    public void ExportAll(string directory)
    {
        DirAccess.MakeDirRecursiveAbsolute(directory);
        foreach (var (name, stream) in _sounds)
        {
            var error = stream.SaveToWav($"{directory}/{name}.wav");
            GD.Print(error == Error.Ok ? $"Exported {name}.wav" : $"Export of {name} failed: {error}");
        }
    }
}
