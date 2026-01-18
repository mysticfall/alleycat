using System.Text.Json.Serialization;
using AlleyCat.Actor;
using AlleyCat.Speech;

namespace AlleyCat.Ai;

[JsonPolymorphic]
public interface IObservation
{
    DateTime Timestamp { get; }
}

public interface IObservedActivity : IObservation
{
    ActorId Actor { get; }
}

public readonly record struct ObservedSpeech(
    ActorId Actor,
    ActorId? Target,
    DialogueText Dialogue,
    DateTime Timestamp
) : IObservedActivity;