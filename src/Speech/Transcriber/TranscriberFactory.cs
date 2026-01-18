using AlleyCat.Service.Typed;
using Godot;

namespace AlleyCat.Speech.Transcriber;

[GlobalClass]
public abstract partial class TranscriberFactory : ResourceFactory<ITranscriber>;