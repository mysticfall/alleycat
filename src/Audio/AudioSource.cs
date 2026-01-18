using Godot;

namespace AlleyCat.Audio;

public interface IAudioSource
{
    AudioStreamPlayer3D AudioPlayer { get; }
}