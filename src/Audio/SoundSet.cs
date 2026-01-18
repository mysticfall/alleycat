using Godot;
using static LanguageExt.Prelude;

namespace AlleyCat.Audio;

[GlobalClass]
public partial class SoundSet : Resource
{
    [Export] public AudioStreamWav[] Streams { get; set; } = [];

    [Export(PropertyHint.Range, "-50,50")] public float VolumeDb { get; set; }

    [Export(PropertyHint.Range, "0,10")] public float MaxAttenuation { get; set; }

    public void Play(AudioStreamPlayer3D player)
    {
        var stream = Streams[random(Streams.Length)];
        var volume = VolumeDb - MaxAttenuation * random(101) / 100f;

        player.VolumeDb = volume;
        player.Stream = stream;

        player.Play();
    }
}